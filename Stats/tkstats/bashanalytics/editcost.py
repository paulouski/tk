"""
Edit-cost report: sums Edit/MultiEdit/Write old_string/new_string/content
character costs from raw transcripts, split main vs subagent.
"""

from __future__ import annotations

from dataclasses import dataclass

from tkstats.bashanalytics.classify import _event_in_scope
from tkstats.bashanalytics.common import EDIT_TOOL_NAMES
from tkstats.bashanalytics.io import _iter_transcript_tool_uses
from tkstats.ingestion.claude import _projects_dir


@dataclass
class EditOp:
    tool: str
    file_path: str
    old_chars: int
    new_chars: int


def _edit_ops_from_tool_use(block: dict) -> tuple[list[EditOp], int]:
    """Return (edit_ops, write_chars) for one Edit/MultiEdit/Write tool_use block."""
    name = block.get("name")
    tool_input = block.get("input") or {}
    file_path = tool_input.get("file_path", "") or ""

    if name == "Edit":
        old = tool_input.get("old_string", "") or ""
        new = tool_input.get("new_string", "") or ""
        return [EditOp("Edit", file_path, len(old), len(new))], 0

    if name == "MultiEdit":
        ops = []
        for edit in tool_input.get("edits") or []:
            if not isinstance(edit, dict):
                continue
            old = edit.get("old_string", "") or ""
            new = edit.get("new_string", "") or ""
            ops.append(EditOp("MultiEdit", file_path, len(old), len(new)))
        return ops, 0

    if name == "Write":
        content = tool_input.get("content", "") or ""
        return [], len(content)

    return [], 0


@dataclass
class EditCostBucket:
    edit_events: int = 0
    multiedit_events: int = 0
    multiedit_ops: int = 0
    write_events: int = 0
    old_chars: int = 0
    new_chars: int = 0
    write_chars: int = 0

    def add(self, other: "EditCostBucket") -> None:
        self.edit_events += other.edit_events
        self.multiedit_events += other.multiedit_events
        self.multiedit_ops += other.multiedit_ops
        self.write_events += other.write_events
        self.old_chars += other.old_chars
        self.new_chars += other.new_chars
        self.write_chars += other.write_chars

    def to_dict(self) -> dict:
        return {
            "edit_events": self.edit_events,
            "multiedit_events": self.multiedit_events,
            "multiedit_ops": self.multiedit_ops,
            "write_events": self.write_events,
            "old_chars": self.old_chars,
            "new_chars": self.new_chars,
            "write_chars": self.write_chars,
        }


def build_edit_cost_report(model: dict, version: str, scope: str) -> dict:
    """Sum Edit/MultiEdit/Write old_string/new_string/content chars from raw transcripts,
    split main vs subagent, for sessions matching `version` and `scope`."""
    projects_dir = _projects_dir()
    main_bucket = EditCostBucket()
    sub_bucket = EditCostBucket()
    top_edits: list[dict] = []
    sessions_with_edits = 0

    for session in model.get("sessions", []):
        if session.get("tk_version", "unknown") != version:
            continue
        if not _event_in_scope(session, scope):
            continue
        session_file = session.get("session_file")
        if not session_file:
            continue

        is_subagent = bool(session.get("is_subagent"))
        bucket = sub_bucket if is_subagent else main_bucket
        path = projects_dir / session_file
        found_any = False

        for block in _iter_transcript_tool_uses(path, EDIT_TOOL_NAMES):
            found_any = True
            name = block.get("name")
            ops, write_chars = _edit_ops_from_tool_use(block)

            if name == "Edit":
                bucket.edit_events += 1
            elif name == "MultiEdit":
                bucket.multiedit_events += 1
                bucket.multiedit_ops += len(ops)
            elif name == "Write":
                bucket.write_events += 1
                bucket.write_chars += write_chars

            for op in ops:
                bucket.old_chars += op.old_chars
                bucket.new_chars += op.new_chars
                top_edits.append({
                    "tool": op.tool,
                    "file": op.file_path,
                    "old_chars": op.old_chars,
                    "new_chars": op.new_chars,
                    "subagent": is_subagent,
                })

        if found_any:
            sessions_with_edits += 1

    top_edits.sort(key=lambda e: e["old_chars"] + e["new_chars"], reverse=True)

    total_bucket = EditCostBucket()
    total_bucket.add(main_bucket)
    total_bucket.add(sub_bucket)

    return {
        "version": version,
        "scope": scope,
        "sessions_with_edits": sessions_with_edits,
        "main": main_bucket.to_dict(),
        "subagents": sub_bucket.to_dict(),
        "total": total_bucket.to_dict(),
        "old_string_total_chars": total_bucket.old_chars,
        "top_edits": top_edits[:5],
    }


def print_edit_cost_text(report: dict) -> None:
    print(
        f"version={report['version']} scope={report['scope']} "
        f"sessions_with_edits={report['sessions_with_edits']}"
    )
    print()
    for name in ("main", "subagents", "total"):
        b = report[name]
        print(
            f"  {name:<10} edit={b['edit_events']:>5} "
            f"multiedit={b['multiedit_events']:>4}(ops={b['multiedit_ops']:>5}) "
            f"write={b['write_events']:>5} "
            f"old_chars={b['old_chars']:>9} new_chars={b['new_chars']:>9} "
            f"write_chars={b['write_chars']:>9}"
        )
    print()
    print(
        f"old_string_total_chars={report['old_string_total_chars']} "
        "(upper bound of output-token savings if line-anchored patching replaced old_string echoing)"
    )
    print()
    print("Top 5 largest single edits:")
    for e in report["top_edits"]:
        where = "sub" if e["subagent"] else "main"
        print(
            f"  {where} {e['tool']:<9} old={e['old_chars']:>7} new={e['new_chars']:>7} {e['file']}"
        )
