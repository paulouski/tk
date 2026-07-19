"""
Sub-agent delegation-cost group rollup.

Groups sessions by group_id (a main session plus its sub-agents) and
computes the counterfactual cost of doing the sub-agents' work inline in the
main session, as a range estimate.
"""

from __future__ import annotations

from collections import defaultdict

from tkstats.pricing import cost_tokens_by_type, is_known_model


def _build_groups(session_models: list[dict]) -> list[dict]:
    """
    Group sessions by group_id and compute delegation economics per group.
    """
    # Collect by group_id
    by_group: dict[str, list[dict]] = defaultdict(list)
    for sm in session_models:
        gid = sm.get("group_id") or sm.get("session_id", "")
        by_group[gid].append(sm)

    groups: list[dict] = []
    for gid, members in by_group.items():
        mains = [m for m in members if not m.get("is_subagent")]
        subs  = [m for m in members if m.get("is_subagent")]

        main_sm = mains[0] if mains else None
        main_session_id = main_sm.get("session_id") if main_sm else None

        def _cost(sm: dict) -> dict:
            return sm.get("cost") or {}

        def _tok(sm: dict) -> dict:
            return _cost(sm).get("tokens") or {}

        def _usd_by(sm: dict) -> dict:
            return _cost(sm).get("usd_by_type") or {}

        main_usd = _cost(main_sm).get("usd", 0.0) if main_sm else 0.0
        subs_usd = sum(_cost(s).get("usd", 0.0) for s in subs)
        total_usd = main_usd + subs_usd

        main_output_usd = _usd_by(main_sm).get("output", 0.0) if main_sm else 0.0
        main_read_usd = (
            (_usd_by(main_sm).get("input", 0.0) or 0.0)
            + (_usd_by(main_sm).get("cache_read", 0.0) or 0.0)
            + (_usd_by(main_sm).get("cache_write", 0.0) or 0.0)
        ) if main_sm else 0.0

        subs_output_usd = sum(_usd_by(s).get("output", 0.0) for s in subs)
        subs_read_usd = sum(
            (_usd_by(s).get("input", 0.0) or 0.0)
            + (_usd_by(s).get("cache_read", 0.0) or 0.0)
            + (_usd_by(s).get("cache_write", 0.0) or 0.0)
            for s in subs
        )

        # A group with any un-priced (unknown/empty) model has an understated
        # cost, which would silently inflate the delegation delta in the optimistic
        # direction. Flag it and skip the delta so it can't anchor the headline.
        has_unknown_model = any(
            not is_known_model((_cost(m).get("model") or "")) for m in members
        )

        # B5: parent-sub tk operand overlap. When the main session and a sub
        # both touch the same symbols, the sub is reinforcing the main's work
        # rather than exploring something new. Count overlapping operands and
        # keep a short examples list for debugging/dashboard use.
        def _collect_ops(sm: dict | None) -> set[str]:
            if not sm:
                return set()
            ops: set[str] = set()
            for ev in sm.get("events") or []:
                for invocation in (ev.get("tk_invocations") or [ev]):
                    for op in (invocation.get("tk_operand_values") or []):
                        if op:
                            ops.add(op)
            return ops

        if main_sm and subs:
            parent_ops = _collect_ops(main_sm)
            overlap_count = 0
            overlap_examples: list[str] = []
            for sub in subs:
                sub_ops = _collect_ops(sub)
                common = sub_ops & parent_ops
                overlap_count += len(common)
                for op in common:
                    if op not in overlap_examples and len(overlap_examples) < 10:
                        overlap_examples.append(op)
        else:
            overlap_count = 0
            overlap_examples = []

        # Counterfactual inline estimate (upper-bound heuristic)
        inline_estimate_usd: float | None = None
        delegation_delta_usd: float | None = None
        inline_estimate_lower_usd: float | None = None
        delegation_delta_lower_usd: float | None = None
        if main_sm and subs and not has_unknown_model:
            main_model = (_cost(main_sm).get("model") or "").strip()
            if main_model:
                # Sub output tokens, priced at main's output rate
                sub_out_tok = sum(_tok(s).get("output", 0) for s in subs)
                sub_in_tok  = sum(_tok(s).get("input", 0) for s in subs)
                # Unique informational footprint of the subs: tokens written to
                # cache the first time they were seen. cache_read is the same
                # context re-read every turn — an artifact of turn count, not
                # new information — so it is excluded.
                sub_cw_tok  = sum(
                    _tok(s).get("cache_write_5m", 0) + _tok(s).get("cache_write_1h", 0)
                    for s in subs
                )
                sub_unique_tok = sub_in_tok + sub_cw_tok
                out_cost = cost_tokens_by_type(sub_out_tok, "output", main_model)
                # Upper bound: main re-reads every unique token at its full input rate
                inline_sub_cost = out_cost + cost_tokens_by_type(sub_unique_tok, "input", main_model)
                inline_estimate_usd = round(main_usd + inline_sub_cost, 6)
                delegation_delta_usd = round(inline_estimate_usd - total_usd, 6)
                # Lower bound: that same unique material mostly overlaps main's
                # context and is read at the cheap cache_read rate
                inline_sub_cost_lower = out_cost + cost_tokens_by_type(sub_unique_tok, "cache_read", main_model)
                inline_estimate_lower_usd = round(main_usd + inline_sub_cost_lower, 6)
                delegation_delta_lower_usd = round(inline_estimate_lower_usd - total_usd, 6)

        groups.append({
            "group_id": gid,
            "main_session_id": main_session_id,
            "main_usd": round(main_usd, 6),
            "subs_usd": round(subs_usd, 6),
            "total_usd": round(total_usd, 6),
            "n_subs": len(subs),
            "main_output_usd": round(main_output_usd, 6),
            "main_read_usd": round(main_read_usd, 6),
            "subs_output_usd": round(subs_output_usd, 6),
            "subs_read_usd": round(subs_read_usd, 6),
            "inline_estimate_usd": inline_estimate_usd,
            "delegation_delta_usd": delegation_delta_usd,
            "inline_estimate_lower_usd": inline_estimate_lower_usd,
            "delegation_delta_lower_usd": delegation_delta_lower_usd,
            "has_unknown_model": has_unknown_model,
            "parent_sub_operand_overlap": overlap_count,
            "parent_sub_operand_examples": overlap_examples,
            "assumptions": (
                "Range estimate over the subs' unique token footprint (input + cache_write; "
                "cache_read is excluded as a re-read artifact of turn count). Lower bound: "
                "main ingests that material at the cheap cache_read rate (mostly overlaps its "
                "context). Upper bound: main re-reads all of it fresh at its full input rate. "
                "Both add the subs' output tokens at main's output rate."
            ) if subs else None,
        })

    # Sort by total_usd descending
    groups.sort(key=lambda g: g["total_usd"], reverse=True)
    return groups
