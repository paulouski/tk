"""
Bash-opportunity classification report: for every native Bash command in a
joined model, decide whether tk could/should have been used instead
(auto_tk/manual_tk/nudge_tk/new_tk/native_read/keep_native/compound/unknown),
plus grep-pattern shape (symbol vs free-text). Distinct from the edit-cost
and read-cost reports — this is the routing-opportunity view.
"""

from __future__ import annotations

from collections import Counter, defaultdict
from dataclasses import dataclass, field

from tkstats.bashanalytics.classify import (
    _add_command,
    _add_compound_row,
    _classify,
    _classify_event,
    _classify_grep_pattern,
    _compound_patterns,
    _compound_rows,
    _grep_pattern,
    _iter_bash_events,
    _pipeline_suffixes,
)
from tkstats.bashanalytics.common import (
    AUTO_TK,
    COMPOUND,
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
    ROUTABLE_CATEGORIES,
    UNKNOWN,
)
from tkstats.shellsplit import _has_shell_control, _is_compound_event, _split_segments, _strip_prefix


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


def build_opportunities_report(
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
