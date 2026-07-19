"""
Read-cost report: sums Read tool_use/tool_result sizes from raw transcripts,
split main vs subagent, tracking requested offset/limit shape, hook auto-cap
correlation, and repeat-read behavior.
"""

from __future__ import annotations

from collections import Counter, defaultdict
from dataclasses import dataclass, field

from tkstats.bashanalytics.classify import _event_in_scope
from tkstats.bashanalytics.common import READ_TOOL_NAMES
from tkstats.bashanalytics.io import _iter_transcript_tool_use_results
from tkstats.config import CHARS_BUCKETS, LINE_BUCKETS, MATCH_WINDOW
from tkstats.ingestion.claude import _projects_dir
from tkstats.timeparse import _parse_ts


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
