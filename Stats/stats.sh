#!/bin/bash
set -euo pipefail

SELF="$(cd "$(dirname "$0")" && pwd)"
TPL="$SELF/stats.html"
MODEL="$SELF/result/model.json"
OUT=$(mktemp /tmp/tk-stats-XXXXXX).html
FILTERED_MODEL=$(mktemp /tmp/tk-stats-model-XXXXXX).json
VERSION_FILTER="latest"
REPORT_ARGS=()

usage() {
  cat <<'EOF'
Usage: Stats/stats.sh [--version latest|all|<version>] [--source {claude,opencode,both}] [report.py args...]

Defaults to --version latest. Use --version all to inject the full model.
--source picks which transcript source the rebuilt model.json covers
(default claude; pass both to merge claude+opencode).
Examples:
  Stats/stats.sh
  Stats/stats.sh --version 0.6.0
  Stats/stats.sh --version all --source both --from 2026-07-01
EOF
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --version|-v)
      if [ "$#" -lt 2 ]; then
        echo "Error: $1 requires a value: latest, all, or a tk version." >&2
        exit 1
      fi
      VERSION_FILTER="$2"
      shift 2
      ;;
    --version=*)
      VERSION_FILTER="${1#--version=}"
      shift
      ;;
    --all-versions)
      VERSION_FILTER="all"
      shift
      ;;
    --source|-s)
      if [ "$#" -lt 2 ]; then
        echo "Error: $1 requires a value: claude, opencode, or both." >&2
        exit 1
      fi
      REPORT_ARGS+=("--source" "$2")
      shift 2
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      REPORT_ARGS+=("$1")
      shift
      ;;
  esac
done

# Rebuild model.json from fresh transcripts (ingest -> join -> report).
echo "Building model from latest chats..."
if [ "${#REPORT_ARGS[@]}" -gt 0 ]; then
  python3 "$SELF/report.py" "${REPORT_ARGS[@]}"
else
  python3 "$SELF/report.py"
fi

if [ ! -f "$MODEL" ]; then
  echo "Error: $MODEL not found. Run the Python pipeline first to generate it." >&2
  exit 1
fi

python3 - "$SELF" "$MODEL" "$FILTERED_MODEL" "$VERSION_FILTER" <<'PY'
from __future__ import annotations

import json
import pathlib
import sys

sys.path.insert(0, sys.argv[1])
from join import apply_version_filter

_, stats_dir, model_path, out_path, target = sys.argv
model = json.loads(pathlib.Path(model_path).read_text())
try:
    filtered = apply_version_filter(model, target)
except ValueError as e:
    print("Error:", e, file=sys.stderr)
    raise SystemExit(2)
pathlib.Path(out_path).write_text(json.dumps(filtered, indent=2), encoding="utf-8")
print(
    "Version filter: %s (%d sessions, %d tk events)"
    % (filtered.get("version_filter", target), len(filtered.get("sessions", [])), filtered["match_stats"]["total_tk_events"])
)
PY

# Split template at placeholder, inject model.json as JS object.
# Escape "</" so a literal "</script>" inside the data can't close the
# inline <script> early (in JS string context "<\/" is identical to "</").
sed '/\/\*__DATA__\*\//,$d' "$TPL" > "$OUT"
echo 'const model =' >> "$OUT"
sed 's|</|<\\/|g' "$FILTERED_MODEL" >> "$OUT"
echo ';' >> "$OUT"
sed '1,/\/\*__DATA__\*\//d' "$TPL" >> "$OUT"

open "$OUT"
echo "→ $OUT"
