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
Usage: Stats/stats.sh [--version latest|all|<version>] [report.py args...]

Defaults to --version latest. Use --version all to inject the full model.
Examples:
  Stats/stats.sh
  Stats/stats.sh --version 0.6.0
  Stats/stats.sh --version all --from 2026-07-01
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
import sys
from pathlib import Path

_, stats_dir, model_path, out_path, requested = sys.argv
model = json.loads(Path(model_path).read_text())


def version_key(version: str) -> tuple[int, ...]:
    if version == "unknown":
        return (-1,)
    try:
        return tuple(int(part) for part in version.split("."))
    except ValueError:
        return (-1,)


def available_versions() -> list[str]:
    versions = [row.get("version", "unknown") for row in model.get("by_version", [])]
    return [v for v in versions if v]


def latest_version() -> str:
    versions = [v for v in available_versions() if v != "unknown"]
    return max(versions, key=version_key) if versions else "unknown"


target = latest_version() if requested == "latest" else requested
versions = available_versions()
if target != "all" and target not in versions:
    print(
        "Error: tk version %r not found. Available: %s"
        % (target, ", ".join(versions) or "(none)"),
        file=sys.stderr,
    )
    raise SystemExit(2)

if target == "all":
    filtered = model
else:
    sessions = []
    for session in model.get("sessions", []):
        event_versions = {
            ev.get("tk_version")
            for ev in session.get("events", [])
            if ev.get("is_tk") and ev.get("tk_version")
        }
        if session.get("tk_version") != target and target not in event_versions:
            continue
        copy = dict(session)
        copy["events"] = [
            ev for ev in session.get("events", [])
            if not ev.get("is_tk") or ev.get("tk_version") == target
        ]
        copy["n_events"] = len(copy["events"])
        copy["n_tk_events"] = sum(1 for ev in copy["events"] if ev.get("is_tk"))
        sessions.append(copy)

    tk_events = [
        ev
        for session in sessions
        for ev in session.get("events", [])
        if ev.get("is_tk")
    ]
    matched = sum(1 for ev in tk_events if ev.get("raw_chars") is not None)
    outcomes = {"WIN": 0, "NEUTRAL": 0, "NET_NEGATIVE": 0, "UNKNOWN": 0}
    win_raw = win_shown = neg_raw = neg_shown = 0
    by_version: dict[str, dict] = {}

    for ev in tk_events:
        outcome = ev.get("outcome") or "UNKNOWN"
        outcomes[outcome] = outcomes.get(outcome, 0) + 1
        version = ev.get("tk_version") or "unknown"
        row = by_version.setdefault(
            version,
            {
                "version": version,
                "n": 0,
                "WIN": 0,
                "NEUTRAL": 0,
                "NET_NEGATIVE": 0,
                "UNKNOWN": 0,
                "saved_chars": 0,
            },
        )
        row["n"] += 1
        row[outcome] = row.get(outcome, 0) + 1
        if outcome == "WIN":
            raw = ev.get("raw_chars") or 0
            shown = ev.get("shown_chars_ownlog") or 0
            row["saved_chars"] += raw - shown
            win_raw += raw
            win_shown += shown
        elif outcome == "NET_NEGATIVE":
            neg_raw += ev.get("raw_chars") or 0
            neg_shown += ev.get("shown_chars_ownlog") or 0

    all_ts = [
        ev.get("ts")
        for session in sessions
        for ev in session.get("events", [])
        if ev.get("ts")
    ]
    group_ids = {session.get("group_id") for session in sessions}

    filtered = dict(model)
    filtered["version_filter"] = target
    filtered["sessions"] = sessions
    filtered["groups"] = [
        group for group in model.get("groups", [])
        if group.get("group_id") in group_ids
    ]
    filtered["match_stats"] = {
        "total_tk_events": len(tk_events),
        "matched": matched,
        "unmatched": len(tk_events) - matched,
        "match_rate": round(matched / len(tk_events), 4) if tk_events else 0.0,
    }
    filtered["outcome_counts"] = outcomes
    filtered["aggregates"] = {
        "win": {
            "raw_chars": win_raw,
            "shown_chars": win_shown,
            "saved_chars": win_raw - win_shown,
        },
        "net_negative": {
            "raw_chars": neg_raw,
            "shown_chars": neg_shown,
            "extra_chars": neg_shown - neg_raw,
        },
    }
    filtered["generated_for_range"] = dict(model.get("generated_for_range", {}))
    filtered["generated_for_range"]["actual_from"] = min(all_ts) if all_ts else None
    filtered["generated_for_range"]["actual_to"] = max(all_ts) if all_ts else None
    filtered["by_version"] = sorted(
        by_version.values(),
        key=lambda row: version_key(row["version"]),
        reverse=True,
    )

Path(out_path).write_text(json.dumps(filtered, indent=2), encoding="utf-8")
print(
    "Version filter: %s (%d sessions, %d tk events)"
    % (target, len(filtered.get("sessions", [])), filtered["match_stats"]["total_tk_events"])
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
