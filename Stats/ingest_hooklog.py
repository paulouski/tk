"""
Compatibility wrapper — the hook audit-log adapter moved to
tkstats/ingestion/hooklog.py. Kept so `python3 Stats/ingest_hooklog.py
[--from ISO] [--to ISO]` keeps working as a debug entrypoint.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from tkstats.ingestion.hooklog import load_interventions, main  # noqa: F401

if __name__ == "__main__":
    main()
