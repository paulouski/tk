#!/bin/bash
set -euo pipefail

SELF="$(cd "$(dirname "$0")" && pwd)"
TPL="$SELF/stats.html"
MODEL="$SELF/result/model.json"
OUT=$(mktemp /tmp/tk-stats-XXXXXX).html

# Rebuild model.json from fresh transcripts (ingest -> join -> report).
echo "Building model from latest chats..."
python3 "$SELF/report.py" "$@"

if [ ! -f "$MODEL" ]; then
  echo "Error: $MODEL not found. Run the Python pipeline first to generate it." >&2
  exit 1
fi

# Split template at placeholder, inject model.json as JS object.
# Escape "</" so a literal "</script>" inside the data can't close the
# inline <script> early (in JS string context "<\/" is identical to "</").
sed '/\/\*__DATA__\*\//,$d' "$TPL" > "$OUT"
echo 'const model =' >> "$OUT"
sed 's|</|<\\/|g' "$MODEL" >> "$OUT"
echo ';' >> "$OUT"
sed '1,/\/\*__DATA__\*\//d' "$TPL" >> "$OUT"

open "$OUT"
echo "→ $OUT"
