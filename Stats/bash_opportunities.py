"""
Compatibility wrapper — the bash-opportunity/edit-cost/read-cost reports
moved to tkstats/bashanalytics/ (opportunities.py, editcost.py, readcost.py,
cli.py). Kept so `python3 Stats/bash_opportunities.py ...` keeps working.

Usage:
    python3 Stats/bash_opportunities.py
    python3 Stats/bash_opportunities.py --version 0.6.0 --top 30
    python3 Stats/bash_opportunities.py --version 0.6.0 --event-level
    python3 Stats/bash_opportunities.py --version 0.6.0 --event-level --compound
    python3 Stats/bash_opportunities.py --version 0.6.0 --main-only --json
    python3 Stats/bash_opportunities.py --json
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from tkstats.bashanalytics.cli import main  # noqa: F401

if __name__ == "__main__":
    raise SystemExit(main())
