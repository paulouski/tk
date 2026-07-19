"""
PreToolUse hook audit-log adapter.

Parse the tk-route.sh PreToolUse hook audit log (~/.claude/tk-hook-YYYYMMDD.log),
TAB-separated: ts_epoch<TAB>decision<TAB>session_id<TAB>cwd<TAB>command.
See hooks/tk-route.sh _log_decision (~line 42) for the exact format.

decision values: "cap-read", "nudge", "nudge-edit-write" (voluntary,
additionalContext-only hints), "route-tk", "route-rtk" (auto-applied rewrites).

Importable API
--------------
  load_interventions(from_dt=None, to_dt=None) -> list[dict]

CLI
---
  python3 Stats/ingest_hooklog.py [--from ISO] [--to ISO]
"""

from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

from tkstats.timeparse import _parse_cli_dt


def _log_dir() -> Path:
    return Path.home() / ".claude"


def _in_range(dt: datetime, from_dt: datetime | None, to_dt: datetime | None) -> bool:
    if from_dt and dt < from_dt:
        return False
    if to_dt and dt > to_dt:
        return False
    return True


def load_interventions(from_dt: datetime | None = None, to_dt: datetime | None = None) -> list[dict]:
    """
    Parse all tk-hook-*.log files under ~/.claude/, return intervention
    records sorted by ts_epoch. Malformed lines are skipped defensively.

    Each record: {ts_epoch, decision, session_id, cwd, command}.
    """
    log_dir = _log_dir()
    records: list[dict] = []
    for path in sorted(log_dir.glob("tk-hook-*.log")):
        try:
            with open(path, encoding="utf-8", errors="replace") as f:
                for line in f:
                    line = line.rstrip("\n")
                    if not line:
                        continue
                    parts = line.split("\t")
                    if len(parts) != 5:
                        continue
                    ts_raw, decision, session_id, cwd, command = parts
                    if not decision or not session_id:
                        continue
                    try:
                        ts_epoch = float(ts_raw)
                    except ValueError:
                        continue
                    dt = datetime.fromtimestamp(ts_epoch, tz=timezone.utc)
                    if not _in_range(dt, from_dt, to_dt):
                        continue
                    records.append({
                        "ts_epoch": ts_epoch,
                        "decision": decision,
                        "session_id": session_id,
                        "cwd": cwd,
                        "command": command,
                    })
        except OSError:
            continue
    records.sort(key=lambda r: r["ts_epoch"])
    return records


# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------

def main() -> None:
    parser = argparse.ArgumentParser(description="Ingest tk-route.sh hook audit log")
    parser.add_argument("--from", dest="from_dt", metavar="ISO", help="Start date/datetime (inclusive)")
    parser.add_argument("--to", dest="to_dt", metavar="ISO", help="End date/datetime (inclusive)")
    args = parser.parse_args()

    from_dt = _parse_cli_dt(args.from_dt) if args.from_dt else None
    to_dt = _parse_cli_dt(args.to_dt, end_of_day=True) if args.to_dt else None

    records = load_interventions(from_dt, to_dt)
    counts = Counter(r["decision"] for r in records)

    print(f"Interventions: {len(records)}")
    for decision, n in counts.most_common():
        print(f"  {decision:12s} {n}")


if __name__ == "__main__":
    main()
