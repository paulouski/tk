"""
Compatibility wrapper — the matching pipeline moved to tkstats/matching/
(ownlog.py, versions.py, groups.py, pipeline.py). Kept so
`python3 Stats/join.py [--from ISO] [--to ISO] [--source ...]` keeps working
as a debug entrypoint.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from tkstats.matching.pipeline import main, run_join  # noqa: F401
from tkstats.matching.versions import apply_version_filter  # noqa: F401

if __name__ == "__main__":
    main()
