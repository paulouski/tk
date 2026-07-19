from __future__ import annotations

import json
import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from tkstats.ingestion import codex as ingest_codex  # noqa: E402
from tkstats.ingestion import codex_js  # noqa: E402
from tkstats.matching import pipeline  # noqa: E402


def _row(timestamp: str, record_type: str, payload: dict) -> dict:
    return {"timestamp": timestamp, "type": record_type, "payload": payload}


def _write_rollout(root: Path, rows: list[dict]) -> Path:
    path = root / "2026" / "07" / "15" / "rollout-test.jsonl"
    path.parent.mkdir(parents=True)
    path.write_text("\n".join(json.dumps(row) for row in rows), encoding="utf-8")
    return path


class JavascriptExtraction(unittest.TestCase):
    def test_extracts_literal_and_resolved_identifier_commands(self) -> None:
        source = (
            'const command = "tk def Foo";\n'
            "const results = await Promise.all([\n"
            "  tools.exec_command({cmd: command, workdir: '/repo'}),\n"
            "  tools.exec_command({cmd: 'rg \\\"free text\\\" src'}),\n"
            "]);"
        )

        commands, unparsed = codex_js._extract_exec_commands(source)

        self.assertEqual(commands, ["tk def Foo", 'rg "free text" src'])
        self.assertEqual(unparsed, 0)

    def test_dynamic_template_is_reported_as_unparsed(self) -> None:
        commands, unparsed = codex_js._extract_exec_commands(
            "tools.exec_command({cmd: `rg ${pattern} src`})"
        )

        self.assertEqual(commands, [])
        self.assertEqual(unparsed, 1)

    def test_extracts_shorthand_command_and_array_map(self) -> None:
        shorthand, shorthand_unparsed = codex_js._extract_exec_commands(
            "const cmd = `tk git status`; tools.exec_command({cmd, workdir: '/repo'})"
        )
        mapped, mapped_unparsed = codex_js._extract_exec_commands(
            "const cmds = ['tk def Foo', \"tk refs Foo\"]; "
            "const rs = await Promise.all(cmds.map(cmd => "
            "tools.exec_command({cmd, workdir: '/repo'})));"
        )

        self.assertEqual(shorthand, ["tk git status"])
        self.assertEqual(shorthand_unparsed, 0)
        self.assertEqual(mapped, ["tk def Foo", "tk refs Foo"])
        self.assertEqual(mapped_unparsed, 0)

    def test_extracts_json_style_quoted_cmd_property(self) -> None:
        commands, unparsed = codex_js._extract_exec_commands(
            'tools.exec_command({"cmd":"tk git status","workdir":"/repo"})'
        )

        self.assertEqual(commands, ["tk git status"])
        self.assertEqual(unparsed, 0)

    def test_resolves_scalar_template_and_string_loop(self) -> None:
        templated, template_unparsed = codex_js._extract_exec_commands(
            'const pattern = "Foo|Bar"; '
            "tools.exec_command({cmd: `rg -n \"${pattern}\" src`})"
        )
        looped, loop_unparsed = codex_js._extract_exec_commands(
            "for (const filter of ['One', 'Two']) { "
            "tools.exec_command({cmd: `tk test --filter ${filter}`}); }"
        )

        self.assertEqual(templated, ['rg -n "Foo|Bar" src'])
        self.assertEqual(template_unparsed, 0)
        self.assertEqual(
            looped,
            ["tk test --filter One", "tk test --filter Two"],
        )
        self.assertEqual(loop_unparsed, 0)


class RolloutIngestion(unittest.TestCase):
    def test_merges_wait_output_into_originating_exec_event(self) -> None:
        final_output = "Script completed\nWall time 0.0 seconds\nOutput:\nFoo.cs:12"
        rows = [
            _row("2026-07-15T10:00:00Z", "session_meta", {
                "id": "thread-wait", "cwd": "/repo", "originator": "codex-tui",
            }),
            _row("2026-07-15T10:00:01Z", "response_item", {
                "type": "custom_tool_call", "call_id": "outer-1", "name": "exec",
                "input": "const r = await tools.exec_command({cmd: 'tk def Foo'});",
            }),
            _row("2026-07-15T10:00:02Z", "response_item", {
                "type": "custom_tool_call_output", "call_id": "outer-1",
                "output": [{"type": "input_text", "text": (
                    "Script running with cell ID 42\nWall time 10.0 seconds\nOutput:\n"
                )}],
            }),
            _row("2026-07-15T10:00:03Z", "response_item", {
                "type": "function_call", "call_id": "wait-1", "name": "wait",
                "arguments": json.dumps({"cell_id": "42", "yield_time_ms": 20_000}),
            }),
            _row("2026-07-15T10:00:04Z", "response_item", {
                "type": "function_call_output", "call_id": "wait-1",
                "output": [{"type": "input_text", "text": final_output}],
            }),
        ]

        with tempfile.TemporaryDirectory() as tmp:
            path = _write_rollout(Path(tmp), rows)
            session = ingest_codex._parse_session(path)

        self.assertIsNotNone(session)
        self.assertEqual(len(session["events"]), 1)
        self.assertEqual(session["events"][0]["tool_input_summary"], "tk def Foo")
        self.assertEqual(session["events"][0]["shown_chars"], len(final_output))

    def test_consecutive_waits_join_in_order_without_cross_cell_leakage(self) -> None:
        # Regression: a cell polled by several sequential `wait(cell_id)` calls
        # must have its outputs joined onto the ONE originating event, in the
        # order the waits occurred — not just the last wait's output, and not
        # mixed with an unrelated cell polled in the same rollout.
        rows = [
            _row("2026-07-15T10:00:00Z", "session_meta", {
                "id": "thread-multi-wait", "cwd": "/repo", "originator": "codex-tui",
            }),
            # Cell 99: triggered by outer-1, polled by three consecutive waits.
            _row("2026-07-15T10:00:01Z", "response_item", {
                "type": "custom_tool_call", "call_id": "outer-1", "name": "exec",
                "input": "const r = await tools.exec_command({cmd: 'tk def Foo'});",
            }),
            _row("2026-07-15T10:00:02Z", "response_item", {
                "type": "custom_tool_call_output", "call_id": "outer-1",
                "output": [{"type": "input_text", "text": (
                    "Script running with cell ID 99\nWall time 1.0 seconds\nOutput:\n"
                )}],
            }),
            _row("2026-07-15T10:00:03Z", "response_item", {
                "type": "function_call", "call_id": "wait-1", "name": "wait",
                "arguments": json.dumps({"cell_id": "99", "yield_time_ms": 5_000}),
            }),
            _row("2026-07-15T10:00:04Z", "response_item", {
                "type": "function_call_output", "call_id": "wait-1",
                "output": [{"type": "input_text", "text": "chunk-A "}],
            }),
            _row("2026-07-15T10:00:05Z", "response_item", {
                "type": "function_call", "call_id": "wait-2", "name": "wait",
                "arguments": json.dumps({"cell_id": "99", "yield_time_ms": 5_000}),
            }),
            _row("2026-07-15T10:00:06Z", "response_item", {
                "type": "function_call_output", "call_id": "wait-2",
                "output": [{"type": "input_text", "text": "chunk-B "}],
            }),
            _row("2026-07-15T10:00:07Z", "response_item", {
                "type": "function_call", "call_id": "wait-3", "name": "wait",
                "arguments": json.dumps({"cell_id": "99", "yield_time_ms": 5_000}),
            }),
            _row("2026-07-15T10:00:08Z", "response_item", {
                "type": "function_call_output", "call_id": "wait-3",
                "output": [{"type": "input_text", "text": "chunk-C done"}],
            }),
            # Cell 7: triggered by outer-2, polled by a single unrelated wait —
            # must not pick up any of cell 99's chunks.
            _row("2026-07-15T10:00:09Z", "response_item", {
                "type": "custom_tool_call", "call_id": "outer-2", "name": "exec",
                "input": "const r = await tools.exec_command({cmd: 'tk refs Bar'});",
            }),
            _row("2026-07-15T10:00:10Z", "response_item", {
                "type": "custom_tool_call_output", "call_id": "outer-2",
                "output": [{"type": "input_text", "text": (
                    "Script running with cell ID 7\nWall time 1.0 seconds\nOutput:\n"
                )}],
            }),
            _row("2026-07-15T10:00:11Z", "response_item", {
                "type": "function_call", "call_id": "wait-4", "name": "wait",
                "arguments": json.dumps({"cell_id": "7", "yield_time_ms": 5_000}),
            }),
            _row("2026-07-15T10:00:12Z", "response_item", {
                "type": "function_call_output", "call_id": "wait-4",
                "output": [{"type": "input_text", "text": "chunk-X done"}],
            }),
        ]

        with tempfile.TemporaryDirectory() as tmp:
            path = _write_rollout(Path(tmp), rows)
            session = ingest_codex._parse_session(path)

        self.assertIsNotNone(session)
        # All 4 `wait` rows stay suppressed; only the two outer exec calls surface.
        self.assertEqual(len(session["events"]), 2)

        cell_99_event, cell_7_event = session["events"]
        joined = "chunk-A chunk-B chunk-C done"
        self.assertEqual(cell_99_event["tool_input_summary"], "tk def Foo")
        self.assertEqual(cell_99_event["shown_chars"], len(joined))

        self.assertEqual(cell_7_event["tool_input_summary"], "tk refs Bar")
        self.assertEqual(cell_7_event["shown_chars"], len("chunk-X done"))

    def test_normalizes_exec_wrapper_and_direct_exec_command(self) -> None:
        rows = [
            _row("2026-07-15T10:00:00Z", "session_meta", {
                "id": "thread-1",
                "timestamp": "2026-07-15T10:00:00Z",
                "cwd": "/repo",
                "originator": "Claude Code",
                "source": "vscode",
                "cli_version": "0.144.4",
                "git": {"branch": "feature"},
            }),
            _row("2026-07-15T10:00:01Z", "turn_context", {
                "model": "gpt-test",
            }),
            _row("2026-07-15T10:00:02Z", "response_item", {
                "type": "custom_tool_call",
                "call_id": "outer-1",
                "name": "exec",
                "status": "completed",
                "input": (
                    "const rs = await Promise.all(["
                    "tools.exec_command({cmd: 'tk def Foo'}),"
                    "tools.exec_command({cmd: \"rg bar src\"})"
                    "]);"
                ),
            }),
            _row("2026-07-15T10:00:03Z", "response_item", {
                "type": "custom_tool_call_output",
                "call_id": "outer-1",
                "output": [
                    {"type": "input_text", "text": "header\n"},
                    {"type": "input_text", "text": "body"},
                ],
            }),
            _row("2026-07-15T10:00:04Z", "response_item", {
                "type": "function_call",
                "call_id": "direct-1",
                "name": "exec_command",
                "arguments": json.dumps({"cmd": "git status --short"}),
            }),
            _row("2026-07-15T10:00:05Z", "response_item", {
                "type": "function_call_output",
                "call_id": "direct-1",
                "output": "clean",
            }),
            _row("2026-07-15T10:00:06Z", "event_msg", {
                "type": "token_count",
                "info": {"total_token_usage": {
                    "input_tokens": 100,
                    "cached_input_tokens": 70,
                    "output_tokens": 20,
                }},
            }),
        ]

        with tempfile.TemporaryDirectory() as tmp:
            path = _write_rollout(Path(tmp), rows)
            session = ingest_codex._parse_session(path)

        self.assertIsNotNone(session)
        self.assertEqual(session["agent_type"], "codex:companion")
        self.assertTrue(session["is_subagent"])
        self.assertEqual(session["codex_version"], "0.144.4")
        self.assertEqual(session["cost"]["tokens"]["input"], 30)
        self.assertEqual(session["cost"]["tokens"]["cache_read"], 70)
        self.assertEqual(len(session["events"]), 2)

        wrapped, direct = session["events"]
        self.assertEqual(wrapped["tool"], "Bash")
        self.assertEqual(wrapped["tool_input_summary"], "tk def Foo\nrg bar src")
        self.assertEqual(wrapped["nested_tools"], ["exec_command", "exec_command"])
        self.assertEqual(wrapped["shown_chars"], len("header\nbody"))
        self.assertTrue(wrapped["is_tk"])
        self.assertEqual(wrapped["tk_command"], "def")
        self.assertEqual(direct["tool"], "Bash")
        self.assertEqual(direct["tool_input_summary"], "git status --short")
        self.assertFalse(direct["is_tk"])

    def test_date_slice_recomputes_counts(self) -> None:
        rows = [
            _row("2026-07-14T10:00:00Z", "session_meta", {
                "id": "thread-2", "cwd": "/repo", "originator": "codex-tui",
            }),
            _row("2026-07-14T10:00:01Z", "response_item", {
                "type": "function_call", "call_id": "one",
                "name": "exec_command", "arguments": '{"cmd":"tk def Old"}',
            }),
            _row("2026-07-15T10:00:01Z", "response_item", {
                "type": "function_call", "call_id": "two",
                "name": "exec_command", "arguments": '{"cmd":"tk refs New"}',
            }),
        ]
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            _write_rollout(root, rows)
            start = datetime(2026, 7, 15, tzinfo=timezone.utc)
            end = datetime(2026, 7, 15, 23, 59, 59, tzinfo=timezone.utc)
            sessions = ingest_codex.load_sessions(start, end, sessions_dir=root)

        self.assertEqual(len(sessions), 1)
        self.assertEqual(sessions[0]["n_events"], 1)
        self.assertEqual(sessions[0]["n_tk_events"], 1)
        self.assertEqual(sessions[0]["events"][0]["tk_command"], "refs")

    def test_subagent_parent_is_preserved(self) -> None:
        agent_type, is_subagent, parent = ingest_codex._agent_metadata({
            "source": {"subagent": {"thread_spawn": {
                "parent_thread_id": "parent-1",
            }}},
        })

        self.assertEqual(agent_type, "codex:subagent")
        self.assertTrue(is_subagent)
        self.assertEqual(parent, "parent-1")


class SourceRouting(unittest.TestCase):
    def test_codex_source_uses_codex_ingester(self) -> None:
        session = {
            "session_id": "codex-1", "agent_type": "codex", "events": [{
                "ts": "2026-07-15T10:00:00Z", "tool": "Bash",
                "is_tk": False, "tool_input_summary": "git status",
            }],
        }
        with patch("tkstats.matching.pipeline._load_codex_sessions", return_value=[session]) as load_codex, \
             patch("tkstats.matching.pipeline.load_sessions") as load_claude, \
             patch("tkstats.matching.pipeline._load_oc_sessions") as load_opencode, \
             patch("tkstats.matching.pipeline.load_own_log", return_value=[]), \
             patch("tkstats.matching.pipeline.load_interventions", return_value=[]) as load_interventions, \
             patch("tkstats.matching.pipeline._infer_version", return_value="0.10.0"):
            model = pipeline.run_join(source="codex")

        load_codex.assert_called_once_with(None, None)
        load_claude.assert_not_called()
        load_opencode.assert_not_called()
        load_interventions.assert_not_called()
        self.assertEqual(model["sessions"][0]["agent_type"], "codex")
        self.assertEqual(model["sessions"][0]["tk_version"], "0.10.0")


if __name__ == "__main__":
    unittest.main()
