"""
Inspect native Bash calls in Stats/result/model.json and estimate where tk could
have been used instead.

The report is intentionally separate from Stats/report.py: it answers a
different question. report.py measures tk results; this script looks at non-tk
Bash commands in sessions for a selected tk version and groups likely
replacement opportunities.

This module is a thin CLI orchestrator. All classification/aggregation logic
lives in the sibling bash_opp_* modules; the public entrypoints are re-exported
here so existing docs examples that do `python3 Stats/bash_opportunities.py ...`
keep working.

Usage:
    python3 Stats/bash_opportunities.py
    python3 Stats/bash_opportunities.py --version 0.6.0 --top 30
    python3 Stats/bash_opportunities.py --version 0.6.0 --event-level
    python3 Stats/bash_opportunities.py --version 0.6.0 --event-level --compound
    python3 Stats/bash_opportunities.py --version 0.6.0 --main-only --json
    python3 Stats/bash_opportunities.py --json
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

# Allow running from the repo root without installing
sys.path.insert(0, str(Path(__file__).parent))

from bash_opp_common import (  # noqa: E402  (sys.path tweak above)
    LEVEL_EVENT,
    LEVEL_SEGMENT,
    SCOPE_ALL,
    SCOPE_MAIN,
    SCOPE_SUBAGENTS,
    _latest_version,
)
from bash_opp_reports import (  # noqa: E402
    build_edit_cost_report,
    build_read_cost_report,
    build_report,
    print_edit_cost_text,
    print_read_cost_text,
    print_text,
)


DEFAULT_MODEL = Path("Stats/result/model.json")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default=str(DEFAULT_MODEL), help="Path to model.json")
    parser.add_argument("--version", default="latest", help="tk version to inspect, or 'latest'")
    parser.add_argument("--top", type=int, default=20, help="Top command shapes to print")
    parser.add_argument("--examples", type=int, default=3, help="Examples per category")
    parser.add_argument("--include-tk", action="store_true", help="Include Bash events that already invoke tk")
    parser.add_argument("--event-level", action="store_true", help="Classify each Bash event once")
    scope_group = parser.add_mutually_exclusive_group()
    scope_group.add_argument("--main-only", action="store_true", help="Only include main-session Bash events")
    scope_group.add_argument("--subagents-only", action="store_true", help="Only include subagent Bash events")
    parser.add_argument("--compound", action="store_true", help="Print compound Bash breakdown in text mode")
    parser.add_argument(
        "--edit-cost", action="store_true",
        help="Report Edit/MultiEdit/Write old_string/new_string/content char costs instead of the Bash report",
    )
    parser.add_argument(
        "--read-cost", action="store_true",
        help="Report Read tool_use/tool_result sizes and offset/limit use instead of the Bash report",
    )
    parser.add_argument("--json", action="store_true", help="Print JSON instead of text")
    args = parser.parse_args()

    model_path = Path(args.model)
    model = json.loads(model_path.read_text())
    version = _latest_version(model) if args.version == "latest" else args.version
    scope = SCOPE_MAIN if args.main_only else SCOPE_SUBAGENTS if args.subagents_only else SCOPE_ALL

    try:
        if args.edit_cost:
            edit_report = build_edit_cost_report(model, version, scope)
            if args.json:
                print(json.dumps(edit_report, indent=2))
            else:
                print_edit_cost_text(edit_report)
            return 0

        if args.read_cost:
            read_report = build_read_cost_report(model, version, scope)
            if args.json:
                print(json.dumps(read_report, indent=2))
            else:
                print_read_cost_text(read_report)
            return 0

        level = LEVEL_EVENT if args.event_level else LEVEL_SEGMENT
        report = build_report(model, version, args.include_tk, args.examples, level, scope)
        if args.json:
            print(json.dumps(report, indent=2))
        else:
            print_text(report, args.top, args.compound)
    except BrokenPipeError:
        return 0
    return 0


__all__ = [
    "build_report",
    "build_edit_cost_report",
    "build_read_cost_report",
    "print_text",
    "print_edit_cost_text",
    "print_read_cost_text",
]


if __name__ == "__main__":
    raise SystemExit(main())
