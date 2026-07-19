"""
Report builders + text renderers for the three sub-reports:
  - bash opportunities     (build_report / print_text)
  - edit cost              (build_edit_cost_report / print_edit_cost_text)
  - read cost              (build_read_cost_report / print_read_cost_text)

Pure logic, no CLI. The CLI in bash_opportunities.py wires these.
"""

from __future__ import annotations

from collections import Counter, defaultdict
from dataclasses import dataclass, field

from config import CHARS_BUCKETS, LINE_BUCKETS, MATCH_WINDOW
from bash_opp_classify import (
    _add_command,
    _add_compound_row,
    _classify,
    _classify_event,
    _classify_grep_pattern,
    _compound_patterns,
    _compound_rows,
    _event_in_scope,
    _grep_pattern,
    _iter_bash_events,
    _pipeline_suffixes,
)
from bash_opp_common import (
    AUTO_TK,
    COMPOUND,
    EDIT_TOOL_NAMES,
    GREP_COMMANDS,
    GREP_FREE_TEXT,
    GREP_SCOPE_MAIN,
    GREP_SCOPE_OVERALL,
    GREP_SCOPE_SUBAGENTS,
    GREP_SYMBOL_SHAPED,
    KEEP_NATIVE,
    LEVEL_EVENT,
    LEVEL_SEGMENT,
    MANUAL_TK,
    NATIVE_READ,
    NEW_TK,
    NUDGE_TK,
    READ_TOOL_NAMES,
    ROUTABLE_CATEGORIES,
    UNKNOWN,
)
from bash_opp_io import (
    _iter_transcript_tool_use_results,
    _iter_transcript_tool_uses,
)
from ingest import _parse_ts, _projects_dir
from bash_opp_shell import (
    _has_shell_control,
    _is_compound_event,
    _split_segments,
    _strip_prefix,
)


# ---------------------------------------------------------------------------
# Bash opportunities report
# ---------------------------------------------------------------------------

@dataclass
class BucketStats:
    """Segment/event/char counters for one (class, scope) bucket, deduped by event id."""

    n: int = 0
    event_n: int = 0
    shown_chars: int = 0
    _event_seen: set[int] = field(default_factory=set)

    def add(self, event_id: int, shown: int) -> None:
        self.n += 1
        if event_id not in self._event_seen:
            self._event_seen.add(event_id)
            self.event_n += 1
            self.shown_chars += shown

    def to_dict(self) -> dict:
        return {"n": self.n, "event_n": self.event_n, "shown_chars": self.shown_chars}


def build_report(
    model: dict,
    version: str,
    include_tk: bool,
    examples_per_bucket: int,
    level: str,
    scope: str,
) -> dict:
    event_totals = Counter()
    segment_totals = Counter()
    by_norm: dict[str, Counter] = defaultdict(Counter)
    command_chars = Counter()
    examples: dict[str, list[dict]] = defaultdict(list)
    event_shown_chars = Counter()
    segment_shown_chars = Counter()
    sessions_seen: set[str] = set()
    subagent_events = 0
    main_events = 0
    segments_seen = 0
    total_shown_chars = 0
    compound_event_id = 0
    compound_events = 0
    compound_segments = 0
    compound_shown_chars = 0
    compound_multiline_events = 0
    first_counts = Counter()
    first_categories: dict[str, Counter] = defaultdict(Counter)
    first_event_seen: dict[str, set[int]] = defaultdict(set)
    first_event_chars = Counter()
    anywhere_counts = Counter()
    anywhere_categories: dict[str, Counter] = defaultdict(Counter)
    anywhere_event_seen: dict[str, set[int]] = defaultdict(set)
    anywhere_event_chars = Counter()
    routable_counts = Counter()
    routable_categories: dict[str, Counter] = defaultdict(Counter)
    routable_event_seen: dict[str, set[int]] = defaultdict(set)
    routable_event_chars = Counter()
    category_counts = Counter()
    category_event_seen: dict[str, set[int]] = defaultdict(set)
    category_event_chars = Counter()
    pattern_counts = Counter()
    pattern_event_seen: dict[str, set[int]] = defaultdict(set)
    pattern_event_chars = Counter()
    suffix_counts = Counter()
    suffix_event_seen: dict[str, set[int]] = defaultdict(set)
    suffix_event_chars = Counter()
    grep_stats: dict[str, dict[str, BucketStats]] = {
        GREP_SYMBOL_SHAPED: defaultdict(BucketStats),
        GREP_FREE_TEXT: defaultdict(BucketStats),
    }
    grep_examples: dict[str, list[dict]] = defaultdict(list)
    bash_event_id = 0

    for session, ev in _iter_bash_events(model, version, include_tk, scope):
        bash_event_id += 1
        sessions_seen.add(session.get("session_id", ""))
        command = ev.get("tool_input_summary", "")
        if not command:
            continue

        segments = _split_segments(command)
        shell_control = _has_shell_control(command)
        if not segments:
            continue
        is_compound = _is_compound_event(command, segments, shell_control)

        if ev.get("is_subagent"):
            subagent_events += 1
        else:
            main_events += 1

        shown = ev.get("shown_chars") or 0
        total_shown_chars += shown

        event_category, event_norm, event_reason = _classify_event(
            command,
            segments,
            shell_control,
            bool(ev.get("symbol_grep")),
        )
        event_totals[event_category] += 1
        event_shown_chars[event_category] += shown
        if level == LEVEL_EVENT:
            _add_command(by_norm, command_chars, event_norm, event_category, shown)
            if len(examples[event_category]) < examples_per_bucket:
                ex = _example(session, ev, event_reason)
                if ex is not None:
                    examples[event_category].append(ex)

        if is_compound:
            segment_totals[COMPOUND] += 1
            segment_shown_chars[COMPOUND] += shown
            norm = "compound"
            if level == LEVEL_SEGMENT:
                _add_command(by_norm, command_chars, norm, COMPOUND, shown)
            if level == LEVEL_SEGMENT and len(examples[COMPOUND]) < examples_per_bucket:
                ex = _example(session, ev, "split or keep native")
                if ex is not None:
                    examples[COMPOUND].append(ex)

            compound_event_id += 1
            compound_events += 1
            compound_segments += len(segments)
            compound_shown_chars += shown
            if "\n" in command:
                compound_multiline_events += 1

            first_category, first_norm, _ = _classify(command, segments[0], bool(ev.get("symbol_grep")))
            _add_compound_row(
                first_counts,
                first_categories,
                first_event_seen,
                first_event_chars,
                first_norm,
                first_category,
                compound_event_id,
                shown,
            )

            for pattern in _compound_patterns(command):
                _add_compound_row(
                    pattern_counts,
                    None,
                    pattern_event_seen,
                    pattern_event_chars,
                    pattern,
                    "",
                    compound_event_id,
                    shown,
                )

            for suffix in _pipeline_suffixes(command):
                _add_compound_row(
                    suffix_counts,
                    None,
                    suffix_event_seen,
                    suffix_event_chars,
                    suffix,
                    "",
                    compound_event_id,
                    shown,
                )

        for tokens in segments:
            segments_seen += 1
            category, norm, reason = _classify(command, tokens, bool(ev.get("symbol_grep")))
            segment_totals[category] += 1
            segment_shown_chars[category] += shown

            seg_cmd = _strip_prefix(tokens)
            if seg_cmd and seg_cmd[0] in GREP_COMMANDS:
                pattern = _grep_pattern(seg_cmd)
                grep_class = _classify_grep_pattern(pattern)
                grep_scope = GREP_SCOPE_SUBAGENTS if ev.get("is_subagent") else GREP_SCOPE_MAIN
                grep_stats[grep_class][grep_scope].add(bash_event_id, shown)
                grep_stats[grep_class][GREP_SCOPE_OVERALL].add(bash_event_id, shown)
                if len(grep_examples[grep_class]) < examples_per_bucket:
                    gex = _grep_example(session, ev, pattern, grep_class)
                    if gex is not None:
                        grep_examples[grep_class].append(gex)

            if is_compound:
                _add_compound_row(
                    anywhere_counts,
                    anywhere_categories,
                    anywhere_event_seen,
                    anywhere_event_chars,
                    norm,
                    category,
                    compound_event_id,
                    shown,
                )
                if category in ROUTABLE_CATEGORIES:
                    _add_compound_row(
                        routable_counts,
                        routable_categories,
                        routable_event_seen,
                        routable_event_chars,
                        norm,
                        category,
                        compound_event_id,
                        shown,
                    )
                    category_counts[category] += 1
                    if compound_event_id not in category_event_seen[category]:
                        category_event_seen[category].add(compound_event_id)
                        category_event_chars[category] += shown
            if level == LEVEL_SEGMENT:
                _add_command(by_norm, command_chars, norm, category, shown)
            if level == LEVEL_SEGMENT and len(examples[category]) < examples_per_bucket:
                ex = _example(session, ev, reason)
                if ex is not None:
                    examples[category].append(ex)

    active_totals = event_totals if level == LEVEL_EVENT else segment_totals
    active_shown_chars = event_shown_chars if level == LEVEL_EVENT else segment_shown_chars
    shown_chars_basis = (
        "event shown_chars counted once per Bash event"
        if level == LEVEL_EVENT
        else "segment shown_chars; compound commands can count one event in multiple segment categories"
    )

    return {
        "level": level,
        "scope": scope,
        "version": version,
        "sessions": len(sessions_seen),
        "bash_events": main_events + subagent_events,
        "bash_segments": segments_seen,
        "main_bash_events": main_events,
        "subagent_bash_events": subagent_events,
        "event_totals": dict(event_totals),
        "segment_totals": dict(segment_totals),
        "totals": dict(active_totals),
        "total_shown_chars": total_shown_chars,
        "event_shown_chars": dict(event_shown_chars),
        "segment_shown_chars": dict(segment_shown_chars),
        "shown_chars": dict(active_shown_chars),
        "shown_chars_basis": shown_chars_basis,
        "top_commands": _top_commands(by_norm, command_chars),
        "top_commands_by_count": _top_commands(by_norm, command_chars),
        "top_commands_by_shown_chars": _top_commands_by_shown_chars(by_norm, command_chars),
        "compound": {
            "events": compound_events,
            "segments": compound_segments,
            "shown_chars": compound_shown_chars,
            "multi_line_events": compound_multiline_events,
            "top_first_commands": _compound_rows(
                first_counts,
                first_event_seen,
                first_event_chars,
                first_categories,
            ),
            "top_anywhere_commands": _compound_rows(
                anywhere_counts,
                anywhere_event_seen,
                anywhere_event_chars,
                anywhere_categories,
            ),
            "top_routable_commands": _compound_rows(
                routable_counts,
                routable_event_seen,
                routable_event_chars,
                routable_categories,
            ),
            "top_routable_categories": _compound_rows(
                category_counts,
                category_event_seen,
                category_event_chars,
                name="category",
            ),
            "top_patterns": _compound_rows(
                pattern_counts,
                pattern_event_seen,
                pattern_event_chars,
                name="pattern",
            ),
            "top_pipeline_suffixes": _compound_rows(
                suffix_counts,
                suffix_event_seen,
                suffix_event_chars,
            ),
        },
        "grep_classes": {
            klass: {
                GREP_SCOPE_MAIN: grep_stats[klass][GREP_SCOPE_MAIN].to_dict(),
                GREP_SCOPE_SUBAGENTS: grep_stats[klass][GREP_SCOPE_SUBAGENTS].to_dict(),
                GREP_SCOPE_OVERALL: grep_stats[klass][GREP_SCOPE_OVERALL].to_dict(),
                "examples": grep_examples[klass],
            }
            for klass in (GREP_SYMBOL_SHAPED, GREP_FREE_TEXT)
        },
        "examples": dict(examples),
    }


def _example(session: dict, ev: dict, reason: str) -> dict | None:
    # C2: filter out garbage examples — empty or paste-sized commands aren't
    # useful as a category exemplar. Return None so the caller skips it.
    cmd = ev.get("tool_input_summary", "")
    ops = ev.get("tk_operand_values") or []
    if not cmd or len(cmd) > 200:
        return None
    if ops and all(len(o) > 200 for o in ops):
        return None
    return {
        "agent": ev.get("agent_type") or session.get("agent_type"),
        "subagent": bool(ev.get("is_subagent")),
        "cwd": ev.get("cwd") or session.get("cwd"),
        "cmd": cmd,
        "reason": reason,
    }


def _grep_example(session: dict, ev: dict, pattern: str | None, grep_class: str) -> dict | None:
    # C2: filter out garbage examples — empty/oversized pattern or cmd
    # signals a paste or malformed input, not a useful grep example.
    cmd = ev.get("tool_input_summary", "")
    if not cmd or len(cmd) > 200:
        return None
    pat = pattern or ""
    if not pat or len(pat) > 200:
        return None
    return {
        "agent": ev.get("agent_type") or session.get("agent_type"),
        "subagent": bool(ev.get("is_subagent")),
        "cwd": ev.get("cwd") or session.get("cwd"),
        "cmd": cmd,
        "pattern": pat,
        "class": grep_class,
    }


def _top_commands(by_norm: dict[str, Counter], command_chars: Counter) -> list[dict]:
    rows = []
    for norm, counts in by_norm.items():
        total = sum(counts.values())
        category, n = counts.most_common(1)[0]
        rows.append({
            "command": norm,
            "n": total,
            "shown_chars": command_chars[norm],
            "top_category": category,
            "top_category_n": n,
            "categories": dict(counts),
        })
    rows.sort(key=lambda r: r["n"], reverse=True)
    return rows


def _top_commands_by_shown_chars(by_norm: dict[str, Counter], command_chars: Counter) -> list[dict]:
    rows = _top_commands(by_norm, command_chars)
    rows.sort(key=lambda r: r["shown_chars"], reverse=True)
    return rows


# ---------------------------------------------------------------------------
# Edit-cost report (Edit/MultiEdit/Write old_string/new_string sizes)
# ---------------------------------------------------------------------------

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


# ---------------------------------------------------------------------------
# Read-cost report (Read tool_use vs tool_result sizes, offset/limit use)
# ---------------------------------------------------------------------------

# CHARS_BUCKETS / LINE_BUCKETS are imported from config.py (single source of truth).


def _chars_bucket(chars: int) -> str:
    if chars < 5000:
        return "<5k"
    if chars < 15000:
        return "5-15k"
    if chars < 40000:
        return "15-40k"
    return ">40k"


def _lines_bucket(lines: int) -> str:
    if lines <= 400:
        return "<=400"
    if lines <= 500:
        return "401-500"
    if lines <= 600:
        return "501-600"
    if lines <= 700:
        return "601-700"
    if lines <= 800:
        return "701-800"
    if lines <= 1500:
        return "801-1500"
    return ">1500"


@dataclass
class ReadCostBucket:
    read_events: int = 0
    successful_events: int = 0
    error_events: int = 0
    uncapped_events: int = 0  # compatibility: requested with no offset/limit
    known_auto_capped_events: int = 0
    explicit_slice_events: int = 0
    no_known_cap_events: int = 0
    total_chars: int = 0
    successful_chars: int = 0
    error_chars: int = 0
    uncapped_chars: int = 0  # compatibility: requested with no offset/limit
    known_auto_capped_chars: int = 0
    known_auto_caps_with_returned_lines: int = 0
    known_auto_cap_returned_lines_total: int = 0
    repeat_pairs: int = 0  # distinct (session, file) pairs read 2+ times
    repeat_excess_events: int = 0  # reads beyond the first, for those pairs
    exact_slice_repeat_events: int = 0
    whole_file_repeat_events: int = 0
    different_slice_events: int = 0
    uncapped_chars_buckets: Counter = field(default_factory=Counter)
    uncapped_line_buckets: Counter = field(default_factory=Counter)
    known_auto_cap_returned_line_buckets: Counter = field(default_factory=Counter)

    def add(self, other: "ReadCostBucket") -> None:
        self.read_events += other.read_events
        self.successful_events += other.successful_events
        self.error_events += other.error_events
        self.uncapped_events += other.uncapped_events
        self.known_auto_capped_events += other.known_auto_capped_events
        self.explicit_slice_events += other.explicit_slice_events
        self.no_known_cap_events += other.no_known_cap_events
        self.total_chars += other.total_chars
        self.successful_chars += other.successful_chars
        self.error_chars += other.error_chars
        self.uncapped_chars += other.uncapped_chars
        self.known_auto_capped_chars += other.known_auto_capped_chars
        self.known_auto_caps_with_returned_lines += other.known_auto_caps_with_returned_lines
        self.known_auto_cap_returned_lines_total += other.known_auto_cap_returned_lines_total
        self.repeat_pairs += other.repeat_pairs
        self.repeat_excess_events += other.repeat_excess_events
        self.exact_slice_repeat_events += other.exact_slice_repeat_events
        self.whole_file_repeat_events += other.whole_file_repeat_events
        self.different_slice_events += other.different_slice_events
        self.uncapped_chars_buckets.update(other.uncapped_chars_buckets)
        self.uncapped_line_buckets.update(other.uncapped_line_buckets)
        self.known_auto_cap_returned_line_buckets.update(
            other.known_auto_cap_returned_line_buckets
        )

    def to_dict(self) -> dict:
        share = self.uncapped_events / self.read_events if self.read_events else 0.0
        return {
            "read_events": self.read_events,
            "successful_events": self.successful_events,
            "error_events": self.error_events,
            "uncapped_events": self.uncapped_events,
            "uncapped_share": share,
            "requested_uncapped_events": self.uncapped_events,
            "requested_uncapped_share": share,
            "known_auto_capped_events": self.known_auto_capped_events,
            "explicit_slice_events": self.explicit_slice_events,
            "no_known_cap_events": self.no_known_cap_events,
            "total_chars": self.total_chars,
            "successful_chars": self.successful_chars,
            "error_chars": self.error_chars,
            "uncapped_chars": self.uncapped_chars,
            "requested_uncapped_chars": self.uncapped_chars,
            "known_auto_capped_chars": self.known_auto_capped_chars,
            "known_auto_caps_with_returned_lines": self.known_auto_caps_with_returned_lines,
            "known_auto_cap_returned_lines_total": self.known_auto_cap_returned_lines_total,
            "repeat_pairs": self.repeat_pairs,
            "repeat_excess_events": self.repeat_excess_events,
            "exact_slice_repeat_events": self.exact_slice_repeat_events,
            "whole_file_repeat_events": self.whole_file_repeat_events,
            "different_slice_events": self.different_slice_events,
            "uncapped_chars_buckets": {k: self.uncapped_chars_buckets.get(k, 0) for k in CHARS_BUCKETS},
            "requested_uncapped_chars_buckets": {
                k: self.uncapped_chars_buckets.get(k, 0) for k in CHARS_BUCKETS
            },
            "uncapped_line_buckets": {k: self.uncapped_line_buckets.get(k, 0) for k in LINE_BUCKETS},
            "requested_uncapped_line_buckets": {
                k: self.uncapped_line_buckets.get(k, 0) for k in LINE_BUCKETS
            },
            "known_auto_cap_returned_line_buckets": {
                k: self.known_auto_cap_returned_line_buckets.get(k, 0)
                for k in LINE_BUCKETS
            },
        }


def _match_cap_intervention(
    rec: dict,
    session_id: str,
    file_path: str,
    interventions: list[dict],
    used: set[int],
) -> bool:
    event_dt = _parse_ts(rec.get("ts"))
    if event_dt is None:
        return False
    event_epoch = event_dt.timestamp()
    candidates = [
        iv for iv in interventions
        if id(iv) not in used
        and iv.get("decision") == "cap-read"
        and iv.get("session_id") == session_id
        and iv.get("command") == file_path
        and iv.get("ts_epoch") is not None
        and abs(event_epoch - iv["ts_epoch"]) <= MATCH_WINDOW.total_seconds()
    ]
    if not candidates:
        return False
    match = min(candidates, key=lambda iv: abs(event_epoch - iv["ts_epoch"]))
    used.add(id(match))
    return True


def build_read_cost_report(model: dict, version: str, scope: str) -> dict:
    """Sum Read tool_use/tool_result sizes from raw transcripts, split main vs subagent,
    for sessions matching `version` and `scope`. Requested offset/limit shape is
    reported separately from known hook auto-caps. Result sidecars may preserve
    effective returned lines, while the configured hook limit is not logged.
    Errors remain in the totals with separate counts."""
    projects_dir = _projects_dir()
    main_bucket = ReadCostBucket()
    sub_bucket = ReadCostBucket()
    top_reads: list[dict] = []
    repeat_offenders: list[dict] = []
    sessions_with_reads = 0
    cap_interventions = model.get("interventions") or []
    used_cap_interventions: set[int] = set()

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
        file_counts: Counter[str] = Counter()
        seen_ranges: dict[str, set[tuple[object, object]]] = {}
        file_repeat_kinds: dict[str, Counter] = defaultdict(Counter)

        for rec in _iter_transcript_tool_use_results(path, READ_TOOL_NAMES):
            found_any = True
            tool_input = rec["tool_input"]
            file_path = tool_input.get("file_path", "") or ""
            offset = tool_input.get("offset")
            limit = tool_input.get("limit")
            requested_uncapped = offset is None and limit is None
            known_auto_cap = requested_uncapped and _match_cap_intervention(
                rec,
                session.get("session_id", ""),
                file_path,
                cap_interventions,
                used_cap_interventions,
            )
            chars = rec["result_chars"]
            returned_lines = rec["num_lines"]
            total_lines = rec["total_lines"]
            lines = total_lines if total_lines is not None else returned_lines

            bucket.read_events += 1
            bucket.total_chars += chars
            if rec["is_error"]:
                bucket.error_events += 1
                bucket.error_chars += chars
            else:
                bucket.successful_events += 1
                bucket.successful_chars += chars
            if requested_uncapped:
                bucket.uncapped_events += 1
                bucket.uncapped_chars += chars
                bucket.uncapped_chars_buckets[_chars_bucket(chars)] += 1
                if lines is not None:
                    bucket.uncapped_line_buckets[_lines_bucket(lines)] += 1
                if known_auto_cap:
                    bucket.known_auto_capped_events += 1
                    bucket.known_auto_capped_chars += chars
                    if returned_lines is not None:
                        bucket.known_auto_caps_with_returned_lines += 1
                        bucket.known_auto_cap_returned_lines_total += returned_lines
                        bucket.known_auto_cap_returned_line_buckets[
                            _lines_bucket(returned_lines)
                        ] += 1
                else:
                    bucket.no_known_cap_events += 1
            else:
                bucket.explicit_slice_events += 1

            repeat_kind = None
            if file_path:
                read_range = (offset, limit)
                prior_ranges = seen_ranges.setdefault(file_path, set())
                if read_range in prior_ranges:
                    if read_range == (None, None):
                        repeat_kind = "whole_file_repeat"
                        bucket.whole_file_repeat_events += 1
                    else:
                        repeat_kind = "exact_slice_repeat"
                        bucket.exact_slice_repeat_events += 1
                elif prior_ranges:
                    repeat_kind = "different_slice"
                    bucket.different_slice_events += 1
                prior_ranges.add(read_range)
                if repeat_kind:
                    file_repeat_kinds[file_path][repeat_kind] += 1

            top_reads.append({
                "file": file_path,
                "chars": chars,
                "lines": lines,
                "returned_lines": returned_lines,
                "total_lines": total_lines,
                "uncapped": requested_uncapped,
                "requested_uncapped": requested_uncapped,
                "known_auto_cap": known_auto_cap,
                "cap_status": "hook_capped" if known_auto_cap else (
                    "no_known_cap" if requested_uncapped else "explicit_slice"
                ),
                "is_error": rec["is_error"],
                "repeat_kind": repeat_kind,
                "subagent": is_subagent,
            })
            if file_path:
                file_counts[file_path] += 1

        if found_any:
            sessions_with_reads += 1

        for file_path, count in file_counts.items():
            if count < 2:
                continue
            bucket.repeat_pairs += 1
            bucket.repeat_excess_events += count - 1
            repeat_offenders.append({
                "file": file_path,
                "count": count,
                "session_file": session_file,
                "subagent": is_subagent,
                "exact_slice_repeats": file_repeat_kinds[file_path]["exact_slice_repeat"],
                "whole_file_repeats": file_repeat_kinds[file_path]["whole_file_repeat"],
                "different_slices": file_repeat_kinds[file_path]["different_slice"],
            })

    top_reads.sort(key=lambda r: r["chars"], reverse=True)
    repeat_offenders.sort(key=lambda r: r["count"], reverse=True)

    total_bucket = ReadCostBucket()
    total_bucket.add(main_bucket)
    total_bucket.add(sub_bucket)

    return {
        "version": version,
        "scope": scope,
        "sessions_with_reads": sessions_with_reads,
        "main": main_bucket.to_dict(),
        "subagents": sub_bucket.to_dict(),
        "total": total_bucket.to_dict(),
        "cap_correlation": {
            "match_window_seconds": MATCH_WINDOW.total_seconds(),
            "effective_limit_recorded": (
                total_bucket.known_auto_caps_with_returned_lines > 0
            ),
            "effective_limit_definition": "lines returned by the Read result sidecar",
            "configured_limit_recorded": False,
            "known_auto_caps_with_returned_lines": (
                total_bucket.known_auto_caps_with_returned_lines
            ),
            "legacy_uncapped_fields_mean_requested_input": True,
        },
        "top_reads": top_reads[:10],
        "repeat_offenders": repeat_offenders[:5],
    }


# ---------------------------------------------------------------------------
# Reporting: text output
# ---------------------------------------------------------------------------

def print_text(report: dict, top: int, show_compound: bool) -> None:
    totals = Counter(report["totals"])
    replaceable = totals[AUTO_TK] + totals[MANUAL_TK] + totals[NUDGE_TK]
    bash_events = report["bash_events"]

    print(
        f"version={report['version']} sessions={report['sessions']} "
        f"level={report['level']} scope={report['scope']} "
        f"bash_events={bash_events} bash_segments={report['bash_segments']}"
    )
    print(f"main={report['main_bash_events']} subagents={report['subagent_bash_events']}")
    print(f"shown_chars={report['total_shown_chars']} basis={report['shown_chars_basis']}")
    print(
        "opportunities "
        f"replaceable={replaceable} auto={totals[AUTO_TK]} manual={totals[MANUAL_TK]} "
        f"nudge={totals[NUDGE_TK]} new_tool={totals[NEW_TK]} native_read={totals[NATIVE_READ]} "
        f"compound={totals[COMPOUND]} keep={totals[KEEP_NATIVE]} unknown={totals[UNKNOWN]}"
    )
    print()
    print("Top Bash command shapes:")
    for row in report["top_commands"][:top]:
        cats = " ".join(f"{k}={v}" for k, v in sorted(row["categories"].items()))
        print(f"  {row['n']:>4} {row['shown_chars']:>9} chars {row['command']:<18} {cats}")

    if show_compound:
        compound = report["compound"]
        print()
        print(
            "Compound events "
            f"events={compound['events']} segments={compound['segments']} "
            f"shown_chars={compound['shown_chars']} multi_line={compound['multi_line_events']}"
        )
        _print_compound_rows("First commands", compound["top_first_commands"], top, "command")
        _print_compound_rows("Commands anywhere", compound["top_anywhere_commands"], top, "command")
        _print_compound_rows("Routable commands anywhere", compound["top_routable_commands"], top, "command")
        _print_compound_rows("Routable categories", compound["top_routable_categories"], top, "category")
        _print_compound_rows("Shell patterns/suffixes", compound["top_patterns"], top, "pattern")
        _print_compound_rows("Pipeline suffix commands", compound["top_pipeline_suffixes"], top, "command")

    print()
    print("Grep pattern classes (grep/rg/egrep/fgrep segments, standalone + inside compounds):")
    for klass in (GREP_SYMBOL_SHAPED, GREP_FREE_TEXT):
        g = report["grep_classes"][klass]
        o = g[GREP_SCOPE_OVERALL]
        m = g[GREP_SCOPE_MAIN]
        s = g[GREP_SCOPE_SUBAGENTS]
        print(
            f"  {klass:<14} n={o['n']:>4} events={o['event_n']:>4} shown_chars={o['shown_chars']:>9} "
            f"(main n={m['n']} events={m['event_n']} | subagents n={s['n']} events={s['event_n']})"
        )
        for item in g["examples"]:
            where = "sub" if item["subagent"] else "main"
            print(f"    - {where} {item['agent']}: pattern={item['pattern']!r} cmd={item['cmd']}")

    print()
    print("Examples:")
    for category in (AUTO_TK, MANUAL_TK, NUDGE_TK, NEW_TK, NATIVE_READ, COMPOUND, UNKNOWN):
        items = report["examples"].get(category, [])
        if not items:
            continue
        print(f"  {category}:")
        for item in items:
            where = "sub" if item["subagent"] else "main"
            print(f"    - {where} {item['agent']}: {item['cmd']}")
            print(f"      {item['reason']}")


def _print_compound_rows(title: str, rows: list[dict], top: int, key: str) -> None:
    if not rows:
        return
    print()
    print(f"{title}:")
    for row in rows[:top]:
        cats = ""
        if "categories" in row:
            cats = " " + " ".join(f"{k}={v}" for k, v in sorted(row["categories"].items()))
        print(
            f"  {row['n']:>4} events={row['event_n']:>4} "
            f"{row['event_shown_chars']:>9} chars {row[key]:<18}{cats}"
        )


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


def print_read_cost_text(report: dict) -> None:
    print(
        f"version={report['version']} scope={report['scope']} "
        f"sessions_with_reads={report['sessions_with_reads']}"
    )
    print()
    for name in ("main", "subagents", "total"):
        b = report[name]
        print(
            f"  {name:<10} reads={b['read_events']:>5} "
            f"ok={b['successful_events']:>5} errors={b['error_events']:>4} "
            f"requested_uncapped={b['requested_uncapped_events']:>5}"
            f"({b['requested_uncapped_share']:.0%}) auto_cap={b['known_auto_capped_events']:>5} "
            f"no_known_cap={b['no_known_cap_events']:>5}"
        )
        print(
            f"             chars total={b['total_chars']:>10} ok={b['successful_chars']:>10} "
            f"errors={b['error_chars']:>8} requested_uncapped={b['requested_uncapped_chars']:>10}"
        )
        print(
            f"             repeats exact_slice={b['exact_slice_repeat_events']:>5} "
            f"whole_file={b['whole_file_repeat_events']:>5} "
            f"different_slice={b['different_slice_events']:>5}"
        )
        chars_line = " ".join(f"{k}={b['uncapped_chars_buckets'][k]}" for k in CHARS_BUCKETS)
        print(f"             requested-uncapped chars dist: {chars_line}")
        lines_line = " ".join(f"{k}={b['uncapped_line_buckets'][k]}" for k in LINE_BUCKETS)
        print(f"             requested-uncapped lines dist: {lines_line}")
        returned_line = " ".join(
            f"{k}={b['known_auto_cap_returned_line_buckets'][k]}" for k in LINE_BUCKETS
        )
        print(
            "             known-auto-cap returned lines dist: "
            f"{returned_line} (observed={b['known_auto_caps_with_returned_lines']})"
        )
    print()
    print("Top 10 largest single reads:")
    for r in report["top_reads"]:
        where = "sub" if r["subagent"] else "main"
        cap = r["cap_status"]
        error = " ERROR" if r["is_error"] else ""
        returned = r["returned_lines"] if r["returned_lines"] is not None else "?"
        total = r["total_lines"] if r["total_lines"] is not None else "?"
        print(
            f"  {where} {cap:<14} chars={r['chars']:>8} "
            f"lines={returned!s:>6}/{total!s:<6}{error} {r['file']}"
        )
    print()
    print("Top 5 repeat-read offenders (same file read 2+ times in one session):")
    for r in report["repeat_offenders"]:
        where = "sub" if r["subagent"] else "main"
        print(
            f"  {where} count={r['count']:>3} exact={r['exact_slice_repeats']} "
            f"whole={r['whole_file_repeats']} different={r['different_slices']} "
            f"{r['file']} ({r['session_file']})"
        )


__all__ = [
    "BucketStats",
    "build_report",
    "_example",
    "_grep_example",
    "_top_commands",
    "_top_commands_by_shown_chars",
    "EditOp",
    "_edit_ops_from_tool_use",
    "EditCostBucket",
    "build_edit_cost_report",
    "CHARS_BUCKETS",
    "LINE_BUCKETS",
    "_chars_bucket",
    "_lines_bucket",
    "ReadCostBucket",
    "build_read_cost_report",
    "print_text",
    "_print_compound_rows",
    "print_edit_cost_text",
    "print_read_cost_text",
]
