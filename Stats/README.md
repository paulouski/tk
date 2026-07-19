# Stats

Analytics for `tk` adoption and usage, mined from AI-agent transcripts (Claude
Code, Codex, OpenCode) plus tk's own command log and the PreToolUse hook's
audit log. Answers questions like: how often is `tk` used instead of raw
`grep`/`cat`/`dotnet build`, does it actually save output size, which nudges
get adopted, what's the delegation cost of sub-agents versus doing the work
inline.

**What this does NOT measure**: correctness of `tk`'s output, user
satisfaction, or anything not observable from a transcript + tk's own log +
the hook audit log. It is usage/cost telemetry, not a quality benchmark.

## Sources

| Source | Reader | Where it reads from |
|---|---|---|
| Claude Code transcripts | `tkstats/ingestion/claude.py` | `~/.claude/projects/**/*.jsonl` |
| Codex CLI transcripts | `tkstats/ingestion/codex.py` (+ `codex_js.py` for JS-literal command reconstruction) | `$CODEX_HOME/sessions` (default `~/.codex/sessions`) |
| OpenCode | `tkstats/ingestion/opencode.py` | opencode's SQLite DB (`~/.local/share/opencode/opencode.db`, override via `OPENCODE_DB`) |
| tk's own log | `tkstats/matching/ownlog.py` | `~/.local/state/tk/analytics/*.jsonl` (or `$XDG_STATE_HOME`) |
| PreToolUse hook audit log | `tkstats/ingestion/hooklog.py` | `~/.claude/tk-hook-YYYYMMDD.log` |

Each transcript reader normalizes its source into the same session/event
shape (`session_id`, `agent_type`, `is_subagent`, `cwd`, `cost`, `events: [...]`)
so everything downstream is source-agnostic. Cross-source-shared logic (tk-
invocation parsing, cost aggregation, empty-result detection) lives once in
`tkstats/ingestion/common.py`, not duplicated per adapter.

## Pipeline

```
tkstats/ingestion/{claude,codex,opencode}.py  — parse transcripts → normalized
              │                                  sessions/events
              ▼
tkstats/matching/pipeline.py (run_join)       — pick source(s), match tk events
              │                                  against own-log entries
              │                                  (tkstats/matching/ownlog.py),
              │                                  infer tk_version from git tags
              │                                  (tkstats/matching/versions.py),
              │                                  roll up sub-agent delegation
              │                                  economics (tkstats/matching/groups.py)
              ▼
tkstats/detect/ (annotate)                    — pure annotation pass: fallback/
              │                                  retry/escalated/empty/symbol_grep/
              │                                  reread/native_grep_retry/stealth_read
              │                                  flags (events.py), hook-adoption
              │                                  pipeline (adoption.py), per-session
              │                                  rollups (rollup.py)
              ▼
tkstats/reports/cli.py (main)                 — orchestrates the above, then
              │                                  builds the compact digest via
              │                                  tkstats/aggregates.py +
              │                                  tkstats/reports/build.py
              ├──▶ Stats/result/model.json    (full annotated model, per-event)
              └──▶ Stats/result/report.json   (compact digest, no per-event lists)

Stats/stats.sh                                — reruns the whole pipeline above
                                                 (via Stats/report.py), then
                                                 filters model.json to one tk
                                                 version (tkstats.matching.
                                                 versions.apply_version_filter)
                                                 and injects it into stats.html
                                                 for the dashboard
```

`tkstats/bashanalytics/` is a separate, parallel analysis: it asks a
different question — "where could native Bash calls have used `tk`
instead" — by walking raw transcripts directly (not `model.json`) for
edit-cost and read-cost reports, and `model.json` for the opportunity
classification report. It is not part of the ingest→join→detect→report
critical path.

## Directory

```
Stats/
  tkstats/                      # the package — all real logic lives here
    config.py                    tunable thresholds/classifier constants
    pricing.py                   USD-per-token price table + cost helpers
    timeparse.py                 shared ISO timestamp parsing + date-range filter
    shellsplit.py                shell command segment splitting / control-structure
                                  detection / normalization (shared by ingestion +
                                  bashanalytics)
    detect/
      __init__.py                 annotate(model) — orchestrates the three below
      events.py                   event-level detectors: fallback/retry/escalated/
                                  empty/symbol_grep/reread/native_grep_retry/
                                  stealth_read (pure, no I/O)
      adoption.py                  S4 hook-adoption pipeline: does the agent obey
                                  tk-route.sh nudges/cap-reads?
      rollup.py                    per-session summary counts from events.py's flags

    aggregates.py                pure per-model aggregation computations used by
                                  reports/build.py (by_command, by_agent_type,
                                  symbol_grep, native_grep_retry, reread, cost
                                  summary, by_version)

    ingestion/
      common.py                  shared cross-source helpers: tk-invocation
                                  parser, content flattening, empty-result
                                  heuristic, per-turn cost aggregation
      claude.py                  Claude Code transcript adapter
      codex.py                   Codex adapter (session parsing, wait/cell_id
                                  continuation joining)
      codex_js.py                self-contained JS-literal/exec-command
                                  reconstruction engine used by codex.py
      opencode.py                OpenCode SQLite adapter
      hooklog.py                 PreToolUse hook audit-log adapter

    matching/
      ownlog.py                  tk own-log loading + own-log matching +
                                  outcome classification (WIN/NEUTRAL/NET_NEGATIVE)
      versions.py                tk release-version inference from git tags +
                                  apply_version_filter (used by stats.sh)
      groups.py                  sub-agent delegation-cost group rollup
      pipeline.py                run_join: source dispatch + orchestrates the
                                  three modules above

    reports/
      build.py                   build_report(model) -> report.json dict
      console.py                 print_summary(report) — human-readable console
                                  output
      cli.py                     CLI entrypoint: full pipeline + writes both
                                  JSON files

    bashanalytics/                the separate "where could tk have been used"
      common.py                   shared constants + version-comparison helpers
                                  (shell-command splitting lives in tkstats/
                                  shellsplit.py above — shared with ingestion)
      classify.py                 classify each Bash segment/event into a
                                  routing category + grep-pattern shape
      io.py                       raw transcript .jsonl walking (tool_use/
                                  tool_result blocks)
      opportunities.py            the bash-opportunity classification report
      editcost.py                 Edit/MultiEdit/Write char-cost report
      readcost.py                 Read tool_use/tool_result size + offset/limit
                                  cap-correlation report
      cli.py                      CLI entrypoint for the three reports above

  # Thin compatibility wrappers. `python3 Stats/<name>.py ...` (the CLI/debug
  # entrypoint) keeps working unchanged. Each wrapper only re-exports its
  # module's public API (main() always, plus e.g. load_sessions/load_events/
  # run_join where that was already the documented Importable API) — private
  # `_`-prefixed helpers (e.g. `_build_report`, `_parse_cli_dt`,
  # `_infer_version`) moved to tkstats.* with NO back-compat shim. Anything
  # importing those internals directly must update to the new tkstats.*
  # path — nothing in this repo does today, but this is not a stable API:
  report.py                       -> tkstats.reports.cli.main()
  bash_opportunities.py           -> tkstats.bashanalytics.cli.main()
  ingest.py                       -> tkstats.ingestion.claude.main()
  ingest_codex.py                 -> tkstats.ingestion.codex.main()
  ingest_opencode.py              -> tkstats.ingestion.opencode.main()
  ingest_hooklog.py               -> tkstats.ingestion.hooklog.main()
  join.py                         -> tkstats.matching.pipeline.main()

  stats.sh                        # dashboard build/launch (bash)
  stats.html                      # static dashboard template; data injected
                                   # inline by stats.sh (no fetch())
  result/                         # generated output (model.json, report.json)
                                   # — gitignored
  tests/                          # mirrors tkstats/ package boundaries
```

## Commands

```bash
# Full pipeline: ingest → join → detect → write model.json + report.json + console summary
python3 Stats/report.py [--from ISO] [--to ISO] [--source claude|opencode|codex|both|all]

# Bash-opportunity / edit-cost / read-cost reports (reads Stats/result/model.json by default)
python3 Stats/bash_opportunities.py [--model PATH] [--version latest|<ver>] [--top N] \
    [--examples N] [--include-tk] [--event-level] [--main-only|--subagents-only] \
    [--compound] [--edit-cost] [--read-cost] [--json]

# Rebuild + open the interactive dashboard, filtered to one tk version
Stats/stats.sh [--version latest|all|<version>] [--source claude|opencode|codex|both|all] [report.py args...]

# Debug entrypoints — each source adapter and the matching pipeline can also
# be run standalone for a quick summary:
python3 Stats/ingest.py [--from ISO] [--to ISO]
python3 Stats/ingest_codex.py [--from ISO] [--to ISO]
python3 Stats/ingest_opencode.py [--from ISO] [--to ISO]
python3 Stats/ingest_hooklog.py [--from ISO] [--to ISO]
python3 Stats/join.py [--from ISO] [--to ISO] [--source claude|opencode|codex|both|all]
```

`--source` selects which transcript source(s) feed the pipeline: `claude`
(default), `opencode`, `codex`, `both` (claude+opencode), `all` (all three,
plus hook-log interventions).

`--version`/`-v` on `stats.sh` filters the rebuilt model to one tk release
(inferred from git tag dates, see `tkstats.matching.versions._infer_version`):
`latest` (default, highest known non-unknown version), `all` (no filtering),
or an explicit version string.

## `model.json` / `report.json`

- **`model.json`** — full annotated model: `sessions` (each with its full
  `events` list, cost, per-session rollup counts), `groups` (delegation-cost
  rollup per session-group), `interventions` (hook-log adoption records),
  `match_stats`, `outcome_counts`, `aggregates`, `by_version`, `adoption`.
  Large (can reach 10+ MB) — this is what `stats.html`'s dashboard consumes
  directly (injected inline, not fetched).
- **`report.json`** — compact digest derived from `model.json`: no per-event
  lists. `by_command`, `by_agent_type`, `by_version`, `symbol_grep`,
  `native_grep_retry`, `reread`, `cost`, `tk_invocations`, `adoption`,
  `findings` (top net-negative / fallback-heavy / empty-heavy commands with
  examples).

Both are written to `Stats/result/` and are **gitignored** — always
regenerated by `report.py`, never hand-edited or committed.

## Extending

- **New source adapter**: add `tkstats/ingestion/<name>.py` emitting the same
  normalized session/event shape as `claude.py` (`session_id`, `agent_type`,
  `is_subagent`, `cwd`, `cost`, `events: [...]`), reusing
  `tkstats/ingestion/common.py`'s shared helpers, then wire it into
  `tkstats.matching.pipeline.run_join`'s `source` dispatch and `stats.sh`'s
  `--source` choices.
- **New detector**: add a `_detect_<name>(events)` function to
  `tkstats/detect/events.py` that mutates each event in place, call it from
  `tkstats/detect/__init__.py`'s `annotate()`, and fold its counts into
  `tkstats/detect/rollup.py`'s `_rollup_session`.
- **New report**: either add a block to `tkstats/reports/build.py`'s
  `build_report` (for the main pipeline — pull the aggregation logic into
  `tkstats/aggregates.py` if it's a genuine aggregate over sessions) or a new
  `build_*_report` function in a `tkstats/bashanalytics/` module + a flag in
  `tkstats/bashanalytics/cli.py` (for the separate bash-opportunity analysis).

## Verification

```bash
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s Stats/tests
bash -n Stats/stats.sh
```
