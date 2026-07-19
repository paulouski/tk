"""
Compatibility wrapper — the report pipeline moved to tkstats/reports/
(build.py, console.py, cli.py) + tkstats/aggregates.py. Kept so
`python3 Stats/report.py [--from ISO] [--to ISO] [--source ...]` keeps
working.

Outputs:
    Stats/result/model.json   — full annotated model
    Stats/result/report.json  — compact digest (no per-event lists)
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from tkstats.reports.cli import main  # noqa: F401

if __name__ == "__main__":
    main()
