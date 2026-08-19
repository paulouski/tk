from __future__ import annotations

import json
import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from tkstats.ingestion import claude  # noqa: E402
from tkstats.ingestion import common  # noqa: E402
from tkstats.ingestion import opencode as ingest_opencode  # noqa: E402
from tkstats.bashanalytics import opportunities as bash_opp_reports  # noqa: E402


class TkInvocationParsing(unittest.TestCase):
    def test_newline_records_each_tk_invocation(self) -> None:
        info = common._extract_tk_info("tk def Foo\ntk refs Foo")

        self.assertIsNotNone(info)
        self.assertEqual(info["tk_command"], "def")
        self.assertEqual(info["tk_invocation_count"], 2)
        self.assertEqual(
            [item["tk_command"] for item in info["tk_invocations"]],
            ["def", "refs"],
        )

    def test_semicolon_and_redirection_do_not_pollute_operands(self) -> None:
        info = common._extract_tk_info(
            "echo before; tk diag Foo.cs 2>&1 >/tmp/diag.log; echo after"
        )

        self.assertEqual(info["tk_invocation_count"], 1)
        self.assertEqual(info["tk_operand_values"], ["Foo.cs"])

    def test_quoted_redirect_anchor_remains_an_operand(self) -> None:
        info = common._extract_tk_info('tk focus ">foo" 2>&1 >/tmp/focus.log')

        self.assertEqual(info["tk_operand_values"], [">foo"])

    def test_sequential_relative_cd_accumulates(self) -> None:
        info = common._extract_tk_info(
            "cd services && cd customer && tk diag Foo.cs",
            session_cwd="/repo",
        )

        self.assertEqual(info["effective_cwd"], "/repo/services/customer")

    def test_pipeline_and_later_tk_are_both_recorded(self) -> None:
        info = common._extract_tk_info("tk def Foo | head -20; tk refs Foo")

        self.assertEqual(info["tk_invocation_count"], 2)
        self.assertEqual(
            [item["tk_operand_values"] for item in info["tk_invocations"]],
            [["Foo"], ["Foo"]],
        )

    def test_heredoc_body_is_not_counted_as_more_commands(self) -> None:
        info = common._extract_tk_info(
            "tk write Foo.cs 1-2 <<'PATCH'\n"
            "tk refs ThisIsContent\n"
            "PATCH"
        )

        self.assertEqual(info["tk_invocation_count"], 1)
        self.assertEqual(info["tk_command"], "write")
        self.assertEqual(info["tk_operand_values"], ["Foo.cs", "1-2"])

    def test_quoted_heredoc_text_does_not_hide_later_command(self) -> None:
        info = common._extract_tk_info('echo "<<EOF"\ntk def Foo')

        self.assertEqual(info["tk_invocation_count"], 1)
        self.assertEqual(info["tk_operand_values"], ["Foo"])

    def test_command_after_real_heredoc_is_still_discovered(self) -> None:
        info = common._extract_tk_info("cat <<EOF\nbody\nEOF\ntk def Foo")

        self.assertEqual(info["tk_invocation_count"], 1)
        self.assertEqual(info["tk_operand_values"], ["Foo"])

    def test_bash_report_does_not_count_heredoc_body_as_commands(self) -> None:
        model = {"sessions": [{
            "session_id": "s1",
            "tk_version": "0.10.0",
            "events": [{
                "tool": "Bash",
                "is_tk": True,
                "is_subagent": False,
                "shown_chars": 10,
                "tool_input_summary": (
                    "tk write Foo.cs 1-2 <<'PATCH'\n"
                    "dotnet build | head -20\n"
                    "PATCH"
                ),
            }],
        }]}

        report = bash_opp_reports.build_opportunities_report(
            model,
            version="0.10.0",
            include_tk=True,
            examples_per_bucket=1,
            level="segment",
            scope="all",
        )

        self.assertEqual(report["bash_segments"], 1)
        self.assertEqual(report["compound"]["events"], 0)
        self.assertFalse(
            any("dotnet" in row["command"] for row in report["top_commands"])
        )

    def test_combined_stdout_stderr_redirects_do_not_become_operands(self) -> None:
        info = common._extract_tk_info("tk def Foo &>/tmp/one &>> /tmp/two")

        self.assertEqual(info["tk_operand_values"], ["Foo"])

    def test_one_bash_tool_use_remains_one_result_paired_event(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            projects_dir = Path(tmp)
            transcript = projects_dir / "session.jsonl"
            lines = [
                {
                    "sessionId": "s1",
                    "timestamp": "2026-07-10T10:00:00Z",
                    "cwd": "/repo",
                    "message": {
                        "role": "assistant",
                        "content": [{
                            "type": "tool_use",
                            "id": "u1",
                            "name": "Bash",
                            "input": {"command": "tk def Foo; tk refs Foo"},
                        }],
                    },
                },
                {
                    "sessionId": "s1",
                    "timestamp": "2026-07-10T10:00:01Z",
                    "message": {
                        "role": "user",
                        "content": [{
                            "type": "tool_result",
                            "tool_use_id": "u1",
                            "content": "combined output",
                        }],
                    },
                },
            ]
            transcript.write_text("\n".join(json.dumps(line) for line in lines))

            session = claude._parse_session(transcript, projects_dir)

        self.assertEqual(len(session["events"]), 1)
        self.assertEqual(session["events"][0]["shown_chars"], len("combined output"))
        self.assertEqual(session["events"][0]["tk_invocation_count"], 2)

    def test_cwd_is_tracked_per_line_not_frozen_on_first_seen(self) -> None:
        # A session file can legitimately switch cwd mid-file (parallel
        # subagent turns, monorepo sibling switches). A later tk invocation
        # must get the cwd active at its own line, not the file's first cwd
        # — a stale cwd breaks own-log ws-hash matching and silently drops
        # exit/duration_ms for that invocation (bug #9/#13 root cause).
        with tempfile.TemporaryDirectory() as tmp:
            projects_dir = Path(tmp)
            transcript = projects_dir / "session.jsonl"
            lines = [
                {
                    "sessionId": "s1",
                    "timestamp": "2026-07-10T10:00:00Z",
                    "cwd": "/repo/parent",
                    "message": {
                        "role": "assistant",
                        "content": [{
                            "type": "tool_use", "id": "u1", "name": "Bash",
                            "input": {"command": "echo hi"},
                        }],
                    },
                },
                {
                    "sessionId": "s1",
                    "timestamp": "2026-07-10T10:00:05Z",
                    "cwd": "/repo/parent/sub",
                    "message": {
                        "role": "assistant",
                        "content": [{
                            "type": "tool_use", "id": "u2", "name": "Bash",
                            "input": {"command": "rm f.txt && tk diag Foo.cs && dotnet build"},
                        }],
                    },
                },
            ]
            transcript.write_text("\n".join(json.dumps(line) for line in lines))

            session = claude._parse_session(transcript, projects_dir)

        tk_event = next(e for e in session["events"] if e.get("is_tk"))
        self.assertEqual(tk_event["cwd"], "/repo/parent/sub")
        self.assertEqual(tk_event["effective_cwd"], "/repo/parent/sub")

    def test_top_commands_counts_every_structured_invocation(self) -> None:
        events = [{
            "is_tk": True,
            "tk_command": "def",
            "tk_invocations": [
                {"tk_command": "def", "tk_sub": None},
                {"tk_command": "git", "tk_sub": "status"},
            ],
        }]

        self.assertEqual(
            dict(claude._top_commands(events)),
            {"def": 1, "git status": 1},
        )
        self.assertEqual(
            dict(ingest_opencode._top_commands(events)),
            {"def": 1, "git status": 1},
        )

    def test_date_slice_recomputes_invocation_count(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            projects_dir = Path(tmp)
            transcript = projects_dir / "session.jsonl"
            transcript.write_text("")
            parsed = {
                "events": [
                    {
                        "ts": "2026-07-10T10:00:00Z", "is_tk": True,
                        "tk_invocation_count": 2,
                    },
                    {
                        "ts": "2026-07-11T10:00:00Z", "is_tk": True,
                        "tk_invocation_count": 3,
                    },
                ],
                "n_events": 2,
                "n_tk_events": 2,
                "n_tk_invocations": 5,
            }
            start = datetime(2026, 7, 10, tzinfo=timezone.utc)
            end = datetime(2026, 7, 10, 23, 59, 59, tzinfo=timezone.utc)
            with patch("tkstats.ingestion.claude._projects_dir", return_value=projects_dir), \
                 patch("tkstats.ingestion.claude._parse_session", return_value=parsed):
                sessions = claude.load_sessions(start, end)

        self.assertEqual(sessions[0]["n_events"], 1)
        self.assertEqual(sessions[0]["n_tk_events"], 1)
        self.assertEqual(sessions[0]["n_tk_invocations"], 2)


if __name__ == "__main__":
    unittest.main()
