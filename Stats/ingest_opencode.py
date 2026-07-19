"""
Compatibility wrapper — the OpenCode adapter moved to
tkstats/ingestion/opencode.py. Kept so `python3 Stats/ingest_opencode.py
[--from ISO] [--to ISO]` keeps working as a debug entrypoint.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from tkstats.ingestion.opencode import load_events, load_sessions, main  # noqa: F401

if __name__ == "__main__":
    main()
