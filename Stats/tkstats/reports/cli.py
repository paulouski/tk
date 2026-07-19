"""
Top-level build CLI. Pipeline: ingest -> join -> detect -> write model.json +
report.json.

Usage:
    python3 Stats/report.py [--from ISO] [--to ISO]

Outputs:
    Stats/result/model.json   — full annotated model
    Stats/result/report.json  — compact digest (no per-event lists)
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from tkstats.detect import annotate
from tkstats.matching.pipeline import run_join
from tkstats.reports.build import build_report
from tkstats.reports.console import print_summary
from tkstats.timeparse import _parse_cli_dt


def main() -> None:
    parser = argparse.ArgumentParser(description="Build annotated model + compact report")
    parser.add_argument("--from", dest="from_dt", metavar="ISO")
    parser.add_argument("--to", dest="to_dt", metavar="ISO")
    parser.add_argument(
        "--source", dest="source",
        choices=("claude", "opencode", "codex", "both", "all"),
                        default="claude",
        help="Data source: claude (default), opencode, codex, both, or all",
    )
    args = parser.parse_args()

    from_dt = _parse_cli_dt(args.from_dt) if args.from_dt else None
    to_dt = _parse_cli_dt(args.to_dt, end_of_day=True) if args.to_dt else None

    # Pipeline: join -> annotate -> report
    model = run_join(from_dt, to_dt, source=args.source)
    annotate(model)

    # Write model.json
    result_dir = Path(__file__).parent.parent.parent / "result"
    result_dir.mkdir(exist_ok=True)
    model_path = result_dir / "model.json"
    with open(model_path, "w", encoding="utf-8") as f:
        json.dump(model, f, indent=2)

    # Build and write report.json
    report = build_report(model)

    # Expose by_version and adoption in model.json so the dashboard can render them
    model["by_version"] = report["by_version"]
    model["adoption"] = report["adoption"]

    # Re-write model.json with by_version included
    with open(model_path, "w", encoding="utf-8") as f:
        json.dump(model, f, indent=2)

    report_path = result_dir / "report.json"
    with open(report_path, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)

    print_summary(report)
    print()
    print(f"model.json:  {model_path}  ({model_path.stat().st_size:,} bytes)")
    print(f"report.json: {report_path}  ({report_path.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
