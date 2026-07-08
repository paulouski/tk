#!/usr/bin/env bash
# tk-route.sh — PreToolUse hook: routes single commands through compact-output
# proxies. `dotnet build|test|restore` and `git status|log` (optionally prefixed
# by `-C <path>` / `-c <key=val>` global flags, or `VAR=val` env-var prefixes)
# are routed to `tk`; any other recognized command falls back to `rtk rewrite`
# (if `rtk` is installed). Also nudges symbol-grep toward `tk def|refs|callers`,
# nudges large whole-file Reads (no offset/limit, >400 lines) toward a slice or
# a symbol lookup, and deterministically caps whole-file Reads over
# READ_CAP_LINES lines (no offset/limit) at that many lines via updatedInput.
#
# Compound commands (&&, ||, ;, |, and bare newlines used as statement
# separators) get a conservative, quote-aware segment rewrite: each `&&`/`||`/
# `;`/newline-delimited pipeline is rewritten as a unit, guarded by a
# three-tier downstream check (see _rewrite_pipeline) so a routed left-hand
# side never silently breaks a downstream content filter (`dotnet test | grep
# FAIL` is NOT naively rewritten). Heredocs, backslash-newline continuations,
# and shell control-flow keywords (if/for/while/...) leave the whole command
# untouched.
#
# Design note: this hook degrades to a no-op — if the running Claude Code version
# ignores `updatedInput`/`additionalContext`, the original command simply runs
# unchanged; nothing breaks.

INPUT=$(cat)

tool=$(printf '%s' "$INPUT" | jq -r '.tool_name // empty' 2>/dev/null)
[[ "$tool" != "Bash" && "$tool" != "Read" ]] && exit 0

# Capture cwd and session_id to tag/key log entries. Log to a single global
# directory (~/.claude/) rather than per-repo, so a globally installed hook never
# drops a log file into every repo's working tree. Each line carries the cwd and
# session_id columns: cwd disambiguates by repo, session_id keys the nudge
# throttles below so a main-agent nudge doesn't suppress nudges for freshly
# spawned subagents (each has its own session_id). The log file rotates daily
# (tk-hook-YYYYMMDD.log) so throttle lookups only ever grep a single day's worth
# of lines instead of an ever-growing file. Old dated files (and any
# pre-rotation tk-hook.log) are left alone — no migration, no deletion.
_cwd=$(printf '%s' "$INPUT" | jq -r '.cwd // empty' 2>/dev/null)
_session_id=$(printf '%s' "$INPUT" | jq -r '.session_id // empty' 2>/dev/null)
_today=$(date +%Y%m%d)
_log_file="${HOME}/.claude/tk-hook-${_today}.log"
_log_decision() {
  local decision="$1" raw_cmd="$2"
  [[ -z "$HOME" ]] && return
  # Collapse newlines to spaces so each entry stays one line.
  local one_line
  one_line=$(printf '%s' "$raw_cmd" | tr '\n' ' ')
  printf '%s\t%s\t%s\t%s\t%s\n' "$(date +%s)" "$decision" "$_session_id" "$_cwd" "$one_line" \
    >> "$_log_file" 2>/dev/null || true
}

# _recently_nudged <decision> — throttle helper shared by the symbol-grep nudge
# and the large-read nudge: true (0) if the last logged line for this exact
# <decision, session_id> pair is under 300s old, so a grep-heavy loop or a
# repeated large read within one session doesn't get re-nudged on every call.
# Keyed by session_id (not just decision) so a nudge in one session never
# suppresses a nudge in a different, freshly spawned session. An unreadable/
# missing log means "not recently nudged" (just nudge).
_recently_nudged() {
  local decision="$1"
  [[ -n "$HOME" && -r "$_log_file" ]] || return 1
  local key
  key=$(printf '\t%s\t%s\t' "$decision" "$_session_id")
  local last_ts
  last_ts=$(grep -F "$key" "$_log_file" 2>/dev/null | tail -1 | cut -f1)
  if [[ "$last_ts" =~ ^[0-9]+$ ]]; then
    local now
    now=$(date +%s)
    (( now - last_ts < 300 )) && return 0
  fi
  return 1
}

# --- emit-json helpers: the only two hookSpecificOutput shapes this hook ever
# produces (a rewrite that changes the command, or a nudge that only attaches
# additionalContext). ---
_emit_rewrite() {
  local new_cmd="$1" hint="${2:-}"
  if [[ -n "$hint" ]]; then
    jq -n --arg c "$new_cmd" --arg h "$hint" \
      '{hookSpecificOutput:{hookEventName:"PreToolUse",permissionDecision:"allow",updatedInput:{command:$c},additionalContext:$h}}'
  else
    jq -n --arg c "$new_cmd" \
      '{hookSpecificOutput:{hookEventName:"PreToolUse",permissionDecision:"allow",updatedInput:{command:$c}}}'
  fi
}

_emit_nudge() {
  local ctx="$1"
  jq -n --arg ctx "$ctx" \
    '{hookSpecificOutput:{hookEventName:"PreToolUse",permissionDecision:"allow",additionalContext:$ctx}}'
}

# Read auto-cap threshold in lines. Chosen from Stats/bash_opportunities.py --read-cost
# on tk 0.6.0 transcripts: of uncapped Read events over 400 lines, a 500-line cutoff
# captures ~71% of their chars (36/60 events), well ahead of 600 (~44%) or 700-800
# (~22%) — the sharpest bulk-capture point in the 400-800 range.
READ_CAP_LINES=500

_emit_read_cap() {
  local limit="$1" ctx="$2"
  jq -n --argjson lim "$limit" --arg ctx "$ctx" \
    '{hookSpecificOutput:{hookEventName:"PreToolUse",permissionDecision:"allow",updatedInput:{limit:$lim},additionalContext:$ctx}}'
}

# --- Read handling: cap large whole-file reads with no offset/limit; nudge smaller-but-
# still-large ones. ---
if [[ "$tool" == "Read" ]]; then
  file_path=$(printf '%s' "$INPUT" | jq -r '.tool_input.file_path // empty' 2>/dev/null)
  offset=$(printf '%s' "$INPUT" | jq -r '.tool_input.offset // empty' 2>/dev/null)
  limit=$(printf '%s' "$INPUT" | jq -r '.tool_input.limit // empty' 2>/dev/null)

  if [[ -z "$offset" && -z "$limit" && -n "$file_path" && -f "$file_path" ]]; then
    _lines=$(wc -l < "$file_path" 2>/dev/null | tr -d '[:space:]')
    if [[ "$_lines" =~ ^[0-9]+$ ]]; then
      if (( _lines > READ_CAP_LINES )); then
        # Deterministic — not throttled: a cap must fire on every qualifying Read, not
        # just the first one per 300s window (unlike the nudge below).
        _log_decision "cap-read" "$file_path"
        _emit_read_cap "$READ_CAP_LINES" "Read capped at ${READ_CAP_LINES} lines by tk-route (file has ${_lines} lines). For C# files, run \`tk view <file>\` to get method ranges, then read the needed offset/limit slice. If you already know the symbol, use \`tk def|refs|callers|impl\`. Re-run with explicit limit=N only when reviewing the whole file."
      elif (( _lines > 400 )); then
        # Throttle: skip if this session was already nudged for a large read within
        # the last 300s. Independent of the symbol-grep "nudge" throttle below —
        # neither suppresses the other.
        _nudge=1
        _recently_nudged "nudge-read" && _nudge=0

        if [[ "$_nudge" -eq 1 ]]; then
          _log_decision "nudge-read" "$file_path"
          _emit_nudge "Large file read without offset/limit detected. If you need one symbol or section, prefer codebase-memory MCP get_code_snippet / \`tk def <Symbol>\` to locate it, or \`tk view <file>\` for a structure map, then Read the relevant slice with offset/limit. Full reads are right when editing or reviewing the whole file."
        fi
      fi
    fi
  fi
  exit 0
fi

# --- Bash handling below (existing routing/nudge logic). ---
cmd=$(printf '%s' "$INPUT" | jq -r '.tool_input.command // empty' 2>/dev/null)
[[ -z "$cmd" ]] && exit 0

# Strip leading whitespace
trimmed="${cmd#"${cmd%%[![:space:]]*}"}"

GIT_GLOBAL_FLAGS_RE='(-C|-c)[[:space:]]+[^[:space:]]*[[:space:]]+'

# Leading run of `VAR=val` environment-variable assignments (one or more,
# space-separated) before the actual command, e.g. `FOO=bar BAZ=qux dotnet build`.
ENV_PREFIX_RE='^(([A-Za-z_][A-Za-z0-9_]*=[^[:space:]]*)[[:space:]]+)+'

# Tier-2 pipeline guard (see _rewrite_pipeline / _grep_segment_is_problem_filter).
PROBLEM_PATTERN_RE='(fail|error|warn|cs[0-9]+|expected|received|assert|passed)'
GREP_FLAG_DENY_CHARS='vcolL'
GREP_LONG_FLAG_DENY_RE='^--(invert-match|count|only-matching|files-with-matches|files-without-match)$'
TIER2_HINT="Pipeline routed to tk; your grep filter was dropped — tk compact output keeps all errors/warnings/failures. Rerun with \`tk --raw dotnet ...\` if you needed the raw stream."

# Shell control-flow keywords: a segment starting with one of these means the
# command is a real shell script, not a simple compound of independent
# commands — conservative passthrough (see hard_unsafe below).
CONTROL_KEYWORD_RE='(^|[;&|]|'$'\n'')[[:space:]]*(if|then|else|elif|fi|for|while|until|do|done|case|esac|function)([[:space:];&|]|$)'

_strip_shell_literals() {
  printf '%s' "$1" | sed -E "s/\\\\.//g; s/'[^']*'//g; s/\"[^\"]*\"//g"
}

# _split_segment <segment> — sets _seg_leading/_seg_core/_seg_trailing to the
# segment's surrounding whitespace and its trimmed body. Shared by
# _rewrite_segment and the pipeline-tier classifiers below.
_split_segment() {
  local segment="$1"
  local core="${segment#"${segment%%[![:space:]]*}"}"
  local leading_len=$(( ${#segment} - ${#core} ))
  _seg_leading="${segment:0:leading_len}"
  local before_trailing="$core"
  core="${before_trailing%"${before_trailing##*[![:space:]]}"}"
  _seg_trailing="${before_trailing:${#core}}"
  _seg_core="$core"
}

# _match_tk_route <rest> — true if `rest` (a command with any env-var prefix
# already stripped) is one of the routable tk commands.
_match_tk_route() {
  local rest="$1"
  [[ "$rest" =~ ^dotnet[[:space:]]+(build|test|restore)([[:space:]]|$) ]] && return 0
  local probe
  probe=$(_strip_shell_literals "$rest")
  # Only status/log. diff/show are deliberately NOT routed: the agent often needs
  # the full diff/patch to understand or apply a change, and tk compacts diffs
  # (and can drop added/changed lines), which would mislead it. A leading run of
  # `-C <path>` / `-c <key=val>` global flags before the subcommand is allowed.
  [[ "$probe" =~ ^git[[:space:]]+(${GIT_GLOBAL_FLAGS_RE})*(status|log)([[:space:]]|$) ]] && return 0
  return 1
}

_rewrite_segment() {
  local segment="$1" allow_rtk="${2:-0}"
  _rewritten_segment=""

  _split_segment "$segment"
  local leading="$_seg_leading" core="$_seg_core" trailing="$_seg_trailing"
  [[ -z "$core" ]] && return 1

  # Strip a leading run of VAR=val environment-variable assignments, so
  # `FOO=bar dotnet build` still routes to `FOO=bar tk dotnet build`. Applies
  # only to the tk routes below; the rtk fallback further down still gets the
  # untouched $core (env prefix and all) — many rtk rewrites are not known to
  # be env-prefix-safe.
  local env_prefix="" rest="$core"
  if [[ "$core" =~ $ENV_PREFIX_RE ]]; then
    env_prefix="${BASH_REMATCH[0]}"
    rest="${core:${#env_prefix}}"
  fi

  # Never double-wrap tk or rtk commands.
  [[ "$rest" == "tk" || "$rest" =~ ^tk[[:space:]] ]] && return 1
  [[ "$rest" == "rtk" || "$rest" =~ ^rtk[[:space:]] ]] && return 1

  if _match_tk_route "$rest"; then
    _rewritten_segment="${leading}${env_prefix}tk ${rest}${trailing}"
    return 0
  fi

  # git diff/show are deliberately excluded from rtk rewriting too: the agent
  # often needs the full diff/patch to understand or apply a change, and
  # compaction can drop added/changed lines, which would mislead it. Same
  # `-C`/`-c` prefix allowance as above, so `git -C <path> diff` still excludes.
  local route_probe
  route_probe=$(_strip_shell_literals "$rest")
  if [[ "$route_probe" =~ ^git[[:space:]]+(${GIT_GLOBAL_FLAGS_RE})*(diff|show)([[:space:]]|$) ]]; then
    return 1
  fi

  [[ "$allow_rtk" -eq 1 ]] || return 1

  # RTK FALLBACK: for anything not routed to tk above, ask rtk if it recognizes
  # the command. rtk exits 0 with the rewritten command on stdout if recognized,
  # or exits 1 with empty stdout otherwise. Skip entirely if rtk isn't installed.
  if command -v rtk >/dev/null 2>&1; then
    local rewritten
    rewritten=$(rtk rewrite "$core" 2>/dev/null)
    if [[ $? -eq 0 && -n "$rewritten" ]]; then
      _rewritten_segment="${leading}${rewritten}${trailing}"
      return 0
    fi
  fi

  return 1
}

# --- Pipeline tier classifiers (three-tier downstream guard) ----------------
#
# A "pipeline" is a maximal run of segments joined only by `|` (as opposed to
# `&&`/`||`/`;`/newline, which start a new, independent pipeline). Naively
# rewriting a pipeline's left-hand side to `tk ...` is only safe when every
# downstream consumer is known not to depend on the raw/verbose text tk
# compacts away:
#
#   Tier 1 — every downstream segment is a display modifier (head/tail/cat):
#            rewrite the LHS only, keep the rest of the pipeline verbatim.
#   Tier 2 — LHS is `dotnet build|test|restore`, every downstream segment is
#            grep/egrep/rg with a problem-shaped pattern and no output-mangling
#            flags (optionally followed by a trailing head/tail): replace the
#            WHOLE pipeline with `tk dotnet <verb> <args>`, dropping the
#            filters, and surface a hint that the filter was dropped.
#   Tier 3 — anything else with a content filter downstream (grep with a
#            non-problem pattern or -v/-c/-o/-l/-L, awk, sed, cut, wc, sort,
#            uniq, xargs): leave the WHOLE pipeline untouched.

_is_display_modifier() {
  _split_segment "$1"
  [[ "$_seg_core" =~ ^(head|tail|cat)([[:space:]]|$) ]]
}

# _shell_words <str> — quote/escape-aware whitespace word split. Sets array
# _words[] with quotes stripped and escapes resolved (best-effort, mirrors the
# quote-tracking already used by _rewrite_compound_command's tokenizer).
_shell_words() {
  local s="$1"
  _words=()
  local cur="" quote="" escaped=0 have_word=0
  local i=0 len=${#s} ch
  while (( i < len )); do
    ch="${s:i:1}"
    if [[ "$escaped" -eq 1 ]]; then
      cur+="$ch"; escaped=0; have_word=1; i=$((i + 1)); continue
    fi
    if [[ "$ch" == "\\" && "$quote" != "'" ]]; then
      escaped=1; have_word=1; i=$((i + 1)); continue
    fi
    if [[ -n "$quote" ]]; then
      if [[ "$ch" == "$quote" ]]; then quote=""; else cur+="$ch"; fi
      have_word=1; i=$((i + 1)); continue
    fi
    if [[ "$ch" == "'" || "$ch" == '"' ]]; then
      quote="$ch"; have_word=1; i=$((i + 1)); continue
    fi
    if [[ "$ch" == " " || "$ch" == $'\t' ]]; then
      if [[ "$have_word" -eq 1 ]]; then
        _words+=("$cur")
        cur=""
        have_word=0
      fi
      i=$((i + 1)); continue
    fi
    cur+="$ch"; have_word=1
    i=$((i + 1))
  done
  [[ "$have_word" -eq 1 ]] && _words+=("$cur")
}

# _grep_segment_is_problem_filter <core> — true if a grep/egrep/rg invocation's
# pattern argument case-insensitively looks problem-shaped (fail/error/warn/
# CSnnnn/expected/received/assert/passed) AND none of its flags are among
# -v/-c/-o/-l/-L (which change grep's output shape away from matching lines).
_grep_segment_is_problem_filter() {
  local core="$1"
  _shell_words "$core"
  local -a words=("${_words[@]}")
  local n=${#words[@]}
  (( n < 1 )) && return 1

  local pattern="" found_pattern=0
  local idx w
  for (( idx = 1; idx < n; idx++ )); do
    w="${words[$idx]}"
    [[ "$w" == "--" ]] && continue
    if [[ "$w" =~ $GREP_LONG_FLAG_DENY_RE ]]; then
      return 1
    fi
    if [[ "$w" == --* ]]; then
      continue  # other long flags (--color=auto, etc.) don't affect output shape
    fi
    if [[ "$w" == -* ]]; then
      local k ch
      for (( k = 1; k < ${#w}; k++ )); do
        ch="${w:k:1}"
        [[ "$GREP_FLAG_DENY_CHARS" == *"$ch"* ]] && return 1
      done
      continue
    fi
    if [[ "$found_pattern" -eq 0 ]]; then
      pattern="$w"
      found_pattern=1
    fi
  done

  [[ "$found_pattern" -eq 1 ]] || return 1
  local match=1
  shopt -s nocasematch
  [[ "$pattern" =~ $PROBLEM_PATTERN_RE ]] || match=0
  shopt -u nocasematch
  [[ "$match" -eq 1 ]]
}

# _pipeline_matches_tier2 <seg1> [seg2 ...] — sets _tier2_rewrite on success.
_pipeline_matches_tier2() {
  local -a segs=("$@")
  local n=${#segs[@]}
  _tier2_rewrite=""

  _split_segment "${segs[0]}"
  local lhs_core="$_seg_core" lhs_leading="$_seg_leading"
  [[ "$lhs_core" =~ ^dotnet[[:space:]]+(build|test|restore)([[:space:]]|$) ]] || return 1

  local j k core
  for (( j = 1; j < n; j++ )); do
    _split_segment "${segs[$j]}"
    core="$_seg_core"

    if [[ "$core" =~ ^(head|tail)([[:space:]]|$) ]]; then
      # A trailing head/tail after the grep filters is allowed and dropped
      # along with them -- but only if EVERY remaining segment from here on is
      # also head/tail (never interleaved before another grep segment).
      for (( k = j; k < n; k++ )); do
        _split_segment "${segs[$k]}"
        [[ "$_seg_core" =~ ^(head|tail)([[:space:]]|$) ]] || return 1
      done
      break
    fi

    if [[ "$core" =~ ^(grep|egrep|rg)[[:space:]] ]]; then
      _grep_segment_is_problem_filter "$core" || return 1
      continue
    fi

    return 1
  done

  _tier2_rewrite="${lhs_leading}tk ${lhs_core}"
  return 0
}

# _rewrite_pipeline <seg1> [seg2 ...] — applies the three-tier guard to one
# pipeline. Sets _rewritten_pipeline (and _pipeline_hint, tier 2 only) on
# success; returns 1 (passthrough) otherwise.
_rewrite_pipeline() {
  _rewritten_pipeline=""
  _pipeline_hint=""
  local -a segs=("$@")
  local n=${#segs[@]}

  if (( n == 1 )); then
    if _rewrite_segment "${segs[0]}" 0; then
      _rewritten_pipeline="$_rewritten_segment"
      return 0
    fi
    return 1
  fi

  # Tier 1: every downstream segment is a display modifier -> rewrite LHS only.
  local all_display=1 j
  for (( j = 1; j < n; j++ )); do
    _is_display_modifier "${segs[$j]}" || { all_display=0; break; }
  done
  if [[ "$all_display" -eq 1 ]]; then
    if _rewrite_segment "${segs[0]}" 0; then
      _rewritten_pipeline="$_rewritten_segment"
      for (( j = 1; j < n; j++ )); do
        _rewritten_pipeline+="|${segs[$j]}"
      done
      return 0
    fi
    return 1
  fi

  # Tier 2: dotnet build|test|restore filtered by problem-shaped grep -> replace
  # the whole pipeline, drop the filters, hint about it.
  if _pipeline_matches_tier2 "${segs[@]}"; then
    _rewritten_pipeline="$_tier2_rewrite"
    _pipeline_hint="$TIER2_HINT"
    return 0
  fi

  # Tier 3: any other content filter downstream -> leave the whole pipeline
  # untouched.
  return 1
}

# _tokenize_compound <input> — quote/escape-aware split on `&&`, `||`, `;`,
# `|`, and bare newlines. Sets _tok_segs[] (segment texts) and _tok_seps[] (the
# separator immediately following each segment; empty string for the last
# segment) and _tok_unterminated_quote.
_tokenize_compound() {
  local input="$1"
  _tok_segs=()
  _tok_seps=()
  _tok_unterminated_quote=0

  local current="" quote="" escaped=0
  local i=0 len=${#input} ch next
  while (( i < len )); do
    ch="${input:i:1}"

    if [[ "$escaped" -eq 1 ]]; then
      current+="$ch"
      escaped=0
      i=$((i + 1))
      continue
    fi

    if [[ "$ch" == "\\" && "$quote" != "'" ]]; then
      current+="$ch"
      escaped=1
      i=$((i + 1))
      continue
    fi

    if [[ -n "$quote" ]]; then
      [[ "$ch" == "$quote" ]] && quote=""
      current+="$ch"
      i=$((i + 1))
      continue
    fi

    if [[ "$ch" == "'" || "$ch" == '"' ]]; then
      quote="$ch"
      current+="$ch"
      i=$((i + 1))
      continue
    fi

    next=""
    (( i + 1 < len )) && next="${input:i+1:1}"
    if [[ "$ch$next" == "&&" || "$ch$next" == "||" ]]; then
      _tok_segs+=("$current")
      _tok_seps+=("$ch$next")
      current=""
      i=$((i + 2))
      continue
    fi

    if [[ "$ch" == ";" || "$ch" == $'\n' ]]; then
      _tok_segs+=("$current")
      _tok_seps+=("$ch")
      current=""
      i=$((i + 1))
      continue
    fi

    if [[ "$ch" == "|" ]]; then
      _tok_segs+=("$current")
      _tok_seps+=("|")
      current=""
      i=$((i + 1))
      continue
    fi

    current+="$ch"
    i=$((i + 1))
  done

  [[ -n "$quote" ]] && _tok_unterminated_quote=1
  _tok_segs+=("$current")
  _tok_seps+=("")
}

_join_pipeline() {
  local out="" first=1 seg
  for seg in "$@"; do
    if [[ "$first" -eq 1 ]]; then out="$seg"; first=0; else out+="|$seg"; fi
  done
  printf '%s' "$out"
}

_rewrite_compound_command() {
  local input="$1"
  _rewritten_compound=""
  _rewritten_compound_hint=""

  _tokenize_compound "$input"
  [[ "$_tok_unterminated_quote" -eq 1 ]] && return 1

  local result="" changed=0 sep piped
  local -a group=()
  local n=${#_tok_segs[@]}
  local idx
  for (( idx = 0; idx < n; idx++ )); do
    group+=("${_tok_segs[$idx]}")
    sep="${_tok_seps[$idx]}"
    if [[ "$sep" == "|" ]]; then
      continue  # still building the same pipeline group
    fi

    if _rewrite_pipeline "${group[@]}"; then
      piped="$_rewritten_pipeline"
      changed=1
      [[ -n "$_pipeline_hint" && -z "$_rewritten_compound_hint" ]] && _rewritten_compound_hint="$_pipeline_hint"
    else
      piped="$(_join_pipeline "${group[@]}")"
    fi
    result+="$piped$sep"
    group=()
  done

  [[ "$changed" -eq 1 ]] || return 1
  _rewritten_compound="$result"
  return 0
}

# Detect shell constructs we still leave alone. Redirects (>, <, 2>&1, &>file)
# are NOT treated as unsafe — prepending `tk` to a redirected single command is
# still correct. Metacharacters inside quotes or backslash-escaped are literal
# text to the shell, not operators, so tests use a stripped copy. Bare
# unquoted newlines are NOT hard_unsafe by themselves (they're a compound
# separator like `;` — see _tokenize_compound); a heredoc, a backslash-newline
# continuation, or a segment starting with a shell control keyword still force
# a full passthrough, conservatively, since those change a newline's meaning.
stripped=$(_strip_shell_literals "$cmd")
hard_unsafe=0
compound=0
case "$stripped" in
  *\'*|*\"*) hard_unsafe=1 ;;  # unbalanced quote survived stripping
  *\`*) hard_unsafe=1 ;;       # backtick command substitution
  *'$('*) hard_unsafe=1 ;;     # $() command substitution
  *'<<'*) hard_unsafe=1 ;;     # heredoc / herestring
esac
[[ "$stripped" =~ \&[[:space:]]*$ ]] && hard_unsafe=1   # trailing background &
[[ "$cmd" == *'\'$'\n'* ]] && hard_unsafe=1              # backslash-newline continuation
[[ "$stripped" =~ $CONTROL_KEYWORD_RE ]] && hard_unsafe=1  # if/for/while/... shell script
case "$stripped" in
  *'&&'*|*'||'*|*\|*|*\;*|*$'\n'*) compound=1 ;;
esac

# REWRITE: safe single commands keep the existing tk/rtk behavior. Compound
# commands get conservative segment-aware tk rewrites only; rtk fallback is left
# to simple commands because many rtk file/stdin rewrites are not pipeline-safe.
if [[ "$hard_unsafe" -eq 0 ]]; then
  if [[ "$compound" -eq 1 ]]; then
    if _rewrite_compound_command "$cmd"; then
      _log_decision "route-tk" "$cmd"
      _emit_rewrite "$_rewritten_compound" "$_rewritten_compound_hint"
      exit 0
    fi
  elif _rewrite_segment "$cmd" 1; then
    decision="route-tk"
    [[ "$_rewritten_segment" =~ ^[[:space:]]*rtk[[:space:]] ]] && decision="route-rtk"
    _log_decision "$decision" "$cmd"
    _emit_rewrite "$_rewritten_segment"
    exit 0
  fi
fi

# NUDGE: suggest semantic symbol nav for grep-for-symbol. Fires after the rewrite paths
# above have declined (a tk/rtk rewrite always wins and exits first) but REGARDLESS of the
# unsafe flag — a symbol-grep buried in a pipe/chain is still worth nudging about, since
# additionalContext never touches the command itself. Tested against the ORIGINAL $cmd
# (not the quote-stripped copy): the symbol pattern usually lives inside quotes.
#
# Two trigger shapes: (1) a keyword (class/record/interface/enum/struct) followed by an
# identifier — the original heuristic; (2) a bare alternation of two capitalized
# identifiers via "|" or backslash-escaped "\|" (e.g. "FooService\|BarService"), which
# usage data showed is a common symbol-hunt shape that (1) alone misses.
SYMBOL_KEYWORD_RE='(class|record|interface|enum|struct)[[:space:]]+[A-Z]'
SYMBOL_ALT_RE='[A-Z][A-Za-z0-9_]*\\?\|[A-Z]'
if [[ "$trimmed" =~ ^(grep|egrep|fgrep|rg)[[:space:]] ]] && \
   { [[ "$cmd" =~ $SYMBOL_KEYWORD_RE ]] || [[ "$cmd" =~ $SYMBOL_ALT_RE ]]; }; then
  # Throttle: skip if this session was already nudged within the last 300s, so a
  # grep-heavy loop doesn't get re-nudged on every call.
  _nudge=1
  _recently_nudged "nudge" && _nudge=0

  if [[ "$_nudge" -eq 1 ]]; then
    _log_decision "nudge" "$cmd"
    _emit_nudge "Symbol lookup via grep detected. Prefer codebase-memory MCP (search_graph/trace_path) for symbol definitions/references/callers; if MCP is unavailable or the repo is not indexed, use \`tk def|refs|callers <Symbol>\` (lsp module). Text grep for \`class X\` misses cross-file references and interface dispatch."
    exit 0
  fi
fi

exit 0
