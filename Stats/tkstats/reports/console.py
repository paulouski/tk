"""
Human-readable console rendering of a report.json dict.
"""

from __future__ import annotations


def print_summary(report: dict) -> None:
    """Print a concise human-readable summary."""
    t = report["totals"]
    oc = report["outcomes"]
    sv = report["savings"]
    rng = report["range"]

    af = rng.get("actual_from") or "?"
    at = rng.get("actual_to") or "?"
    print(f"Range:         {af[:10]} -> {at[:10]}")
    print(f"Sessions:      {t['sessions']}")
    print(
        f"tk events:     {t['tk_events']}  "
        f"(own-log {t['own_log_matched_events']}, {t['own_log_event_match_rate']*100:.1f}%; "
        f"raw baseline {t['raw_baseline_events']}, {t['raw_baseline_event_rate']*100:.1f}%)"
    )
    print(f"tk invocations:{t['tk_invocations']:>7}")
    print(f"Outcomes:      WIN={oc['WIN']}  NEU={oc.get('NEUTRAL', 0)}  NET_NEG={oc['NET_NEGATIVE']}  UNK={oc['UNKNOWN']}")
    print(f"Savings:       saved={sv['win_saved_chars']:,}  extra={sv['net_negative_extra_chars']:,}  net={sv['net_chars']:,}")
    print()

    print("Top primary event commands:")
    for c in report["by_command"][:5]:
        parts = [f"n={c['n']}"]
        if c.get("win"):
            parts.append(f"win={c['win']}")
        if c.get("net_negative"):
            parts.append(f"neg={c['net_negative']}")
        if c.get("fallback_n"):
            parts.append(f"fb={c['fallback_n']}")
        if c.get("result_used_n"):
            parts.append(f"ru={c['result_used_n']}")
        if c.get("empty_n"):
            parts.append(f"empty={c['empty_n']}")
        if c.get("retry_n"):
            parts.append(f"retry={c['retry_n']}")
        if not c.get("applicable", True):
            parts.append("applicable=false")
        print(f"  {c['command']:20s} {', '.join(parts)}")
    print()

    print("Top tk invocations:")
    for item in (report.get("tk_invocations") or {}).get("by_command", [])[:5]:
        print(f"  {item['command']:20s} n={item['n']}")
    print()

    sg = report.get("symbol_grep") or {}
    if sg.get("total"):
        print(f"Symbol greps:  {sg['total']} total (nav gap baseline)")
        for item in (sg.get("top_sessions") or [])[:3]:
            print(f"  session {item['session_id'][:8]}  n={item['n']}")
    print()

    ngr = report.get("native_grep_retry") or {}
    if ngr.get("total"):
        print(f"Grep retries:  {ngr['total']} total (native grep repeated within lookback)")
        for item in (ngr.get("top_sessions") or [])[:3]:
            print(f"  session {item['session_id'][:8]}  n={item['n']}")
    print()

    rr = report.get("reread") or {}
    if rr.get("total"):
        kinds = rr.get("by_kind") or {}
        print(
            f"Exact rereads: {rr['total']} total, {rr['chars_total']:,} chars "
            f"(different slices {kinds.get('different_slice', {}).get('n', 0)})"
        )
        for item in (rr.get("top_sessions") or [])[:3]:
            print(f"  session {item['session_id'][:8]}  n={item['n']}")
    print()

    if report["findings"]:
        print("Findings:")
        for f in report["findings"]:
            print(f"  [{f['type']}] {f['detail']}")

    bv = report.get("by_version") or []
    if bv:
        print()
        print("By tk version:")
        for v in bv:
            ver = v["version"]
            saved = v.get("saved_chars", 0)
            print(
                f"  {ver:10s}  n={v['n']:4d}  WIN={v.get('WIN',0):3d}  "
                f"NEU={v.get('NEUTRAL',0):3d}  NEG={v.get('NET_NEGATIVE',0):3d}  "
                f"UNK={v.get('UNKNOWN',0):3d}  saved={saved:,}"
            )

    cs = report.get("cost") or {}
    if cs:
        print()
        print("Cost (USD):")
        print(f"  Total:  ${cs['total_usd']:.4f}")
        print(f"  Main:   ${cs['main_usd']:.4f}")
        print(f"  Subs:   ${cs['sub_usd']:.4f}")
        top = cs.get("top_groups") or []
        if top:
            print()
            print("  Top groups by cost:")
            for g in top:
                delta = g.get("delegation_delta_usd")
                delta_lower = g.get("delegation_delta_lower_usd")
                inline = g.get("inline_estimate_usd")
                gid_short = (g["group_id"] or "")[:8]
                n_subs = g["n_subs"]
                delta_str = ""
                if delta_lower is not None and delta is not None:
                    lo_sign = "+" if delta_lower >= 0 else ""
                    hi_sign = "+" if delta >= 0 else ""
                    delta_str = f"  delta=[{lo_sign}{delta_lower:.4f} .. {hi_sign}{delta:.4f}] (lower..upper)"
                elif delta is not None:
                    sign = "+" if delta >= 0 else ""
                    delta_str = f"  delta={sign}{delta:.4f} ({'saved' if delta >= 0 else 'overhead'}, upper-bound est)"
                if delta is None and g.get("has_unknown_model"):
                    delta_str = "  delta=n/a (unknown model — cost understated)"
                inline_str = f"  inline≈${inline:.4f}" if inline is not None else ""
                print(
                    f"    {gid_short}  subs={n_subs}"
                    f"  main=${g['main_usd']:.4f}"
                    f"  subs=${g['subs_usd']:.4f}"
                    f"  total=${g['total_usd']:.4f}"
                    f"{inline_str}{delta_str}"
                )
        unknown = cs.get("unknown_models") or []
        if unknown:
            print()
            print(f"  WARNING: unknown models (cost not calculated): {', '.join(unknown)}")

    ad = report.get("adoption") or {}
    by_sub = ad.get("by_subtype") or {}
    auto_routed = ad.get("auto_routed") or {}
    if by_sub or auto_routed.get("n"):
        print()
        print("Hook adoption:")
        for subtype, st in by_sub.items():
            print(
                f"  {subtype:16s} n={st['n_interventions']:4d}  "
                f"adopted={st['n_adopted']:4d}  rate={st['rate']*100:.0f}%"
            )
        if auto_routed.get("n"):
            print(f"  {'auto_routed':16s} n={auto_routed['n']:4d}  (route-tk/route-rtk, no adoption rate)")
