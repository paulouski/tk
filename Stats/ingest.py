"""
Compatibility wrapper — the Claude transcript adapter moved to
tkstats/ingestion/claude.py. Kept so `python3 Stats/ingest.py [--from ISO]
[--to ISO]` keeps working as a debug entrypoint.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from tkstats.ingestion.claude import load_events, load_sessions, main  # noqa: F401

if __name__ == "__main__":
    main()
