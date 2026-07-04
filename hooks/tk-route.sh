#!/usr/bin/env bash
# tk-route.sh — PreToolUse hook: routes single commands through compact-output
# proxies. `dotnet build|test|restore` and `git status|log` are routed to `tk`;
# any other recognized command falls back to `rtk rewrite` (if `rtk` is
# installed). Also nudges symbol-grep toward `tk def|refs|callers`.
#
# Design note: this hook degrades to a no-op — if the running Claude Code version
# ignores `updatedInput`/`additionalContext`, the original command simply runs
# unchanged; nothing breaks. Only single (non-piped, non-chained) commands are
# rewritten; compound commands are left untouched.

INPUT=$(cat)

tool=$(printf '%s' "$INPUT" | jq -r '.tool_name // empty' 2>/dev/null)
[[ "$tool" != "Bash" ]] && exit 0

cmd=$(printf '%s' "$INPUT" | jq -r '.tool_input.command // empty' 2>/dev/null)
[[ -z "$cmd" ]] && exit 0

# Capture cwd to tag log entries. Log to a single global file (~/.claude/) rather
# than per-repo, so a globally installed hook never drops .tk-hook.log into every
# repo's working tree. Each line carries the cwd column to disambiguate by repo.
_cwd=$(printf '%s' "$INPUT" | jq -r '.cwd // empty' 2>/dev/null)
_log_decision() {
  local decision="$1" raw_cmd="$2"
  [[ -z "$HOME" ]] && return
  # Collapse newlines to spaces so each entry stays one line.
  local one_line
  one_line=$(printf '%s' "$raw_cmd" | tr '\n' ' ')
  printf '%s\t%s\t%s\t%s\n' "$(date +%s)" "$decision" "$_cwd" "$one_line" \
    >> "${HOME}/.claude/tk-hook.log" 2>/dev/null || true
}

# Strip leading whitespace
trimmed="${cmd#"${cmd%%[![:space:]]*}"}"

# Never double-wrap tk or rtk commands
[[ "$trimmed" =~ ^tk[[:space:]] ]] && exit 0
[[ "$trimmed" =~ ^rtk[[:space:]] ]] && exit 0

# Detect unsafe commands (pipe, chain, background, subshell, newline).
# Redirects (>, <, 2>&1, &>file) are NOT treated as unsafe — prepending `tk`
# to a redirected single command is still correct.
#
# Metacharacters INSIDE quotes or backslash-escaped are literal text to the
# shell, not operators — `grep "class A\|class B"` is a single safe command.
# Test the checks against a copy with escapes and quoted segments removed,
# so quoted patterns stop false-flagging commands as unsafe. If quotes remain
# unbalanced after stripping, we cannot reason about the rest — treat as unsafe.
stripped=$(printf '%s' "$cmd" | sed -E "s/\\\\.//g; s/'[^']*'//g; s/\"[^\"]*\"//g")
unsafe=0
case "$stripped" in
  *\'*|*\"*) unsafe=1 ;;  # unbalanced quote survived stripping
  *\|*) unsafe=1 ;;       # pipe (also covers ||)
  *\;*) unsafe=1 ;;       # command separator
  *\`*) unsafe=1 ;;       # backtick command substitution
  *'$('*) unsafe=1 ;;     # $() command substitution
  *'&&'*) unsafe=1 ;;     # logical AND chain
esac
[[ "$stripped" =~ \&[[:space:]]*$ ]] && unsafe=1   # trailing background &
[[ "$cmd" == *$'\n'* ]] && unsafe=1

# REWRITE: only for safe, single commands
if [[ "$unsafe" -eq 0 ]]; then
  new=""
  if [[ "$trimmed" =~ ^dotnet[[:space:]]+(build|test|restore)([[:space:]]|$) ]]; then
    new="tk $trimmed"
  # Only status/log. diff/show are deliberately NOT routed: the agent often needs
  # the full diff/patch to understand or apply a change, and tk compacts diffs
  # (and can drop added/changed lines), which would mislead it.
  elif [[ "$trimmed" =~ ^git[[:space:]]+(status|log)([[:space:]]|$) ]]; then
    new="tk $trimmed"
  fi

  if [[ -n "$new" ]]; then
    _log_decision "route-tk" "$cmd"
    jq -n --arg c "$new" \
      '{hookSpecificOutput:{hookEventName:"PreToolUse",permissionDecision:"allow",updatedInput:{command:$c}}}'
    exit 0
  fi

  # git diff/show are deliberately excluded from rtk rewriting too: the agent
  # often needs the full diff/patch to understand or apply a change, and
  # compaction can drop added/changed lines, which would mislead it.
  if [[ "$trimmed" =~ ^git[[:space:]]+(diff|show)([[:space:]]|$) ]]; then
    exit 0
  fi

  # RTK FALLBACK: for anything not routed to tk above, ask rtk if it recognizes
  # the command. rtk exits 0 with the rewritten command on stdout if recognized,
  # or exits 1 with empty stdout otherwise. Skip entirely if rtk isn't installed.
  if command -v rtk >/dev/null 2>&1; then
    rewritten=$(rtk rewrite "$cmd" 2>/dev/null)
    if [[ $? -eq 0 && -n "$rewritten" ]]; then
      _log_decision "route-rtk" "$cmd"
      jq -n --arg c "$rewritten" \
        '{hookSpecificOutput:{hookEventName:"PreToolUse",permissionDecision:"allow",updatedInput:{command:$c}}}'
      exit 0
    fi
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
  # Throttle: skip if the last logged "nudge" is under 300s old, so a grep-heavy loop
  # doesn't get re-nudged on every call. Stateless hook, so this is a log-timestamp check,
  # not a session counter. An unreadable/missing log means "just nudge" (no throttle).
  _nudge=1
  _log_file="${HOME}/.claude/tk-hook.log"
  if [[ -n "$HOME" && -r "$_log_file" ]]; then
    _last_nudge_ts=$(grep -F "$(printf '\tnudge\t')" "$_log_file" 2>/dev/null | tail -1 | cut -f1)
    if [[ "$_last_nudge_ts" =~ ^[0-9]+$ ]]; then
      _now=$(date +%s)
      (( _now - _last_nudge_ts < 300 )) && _nudge=0
    fi
  fi

  if [[ "$_nudge" -eq 1 ]]; then
    _log_decision "nudge" "$cmd"
    jq -n \
      '{hookSpecificOutput:{hookEventName:"PreToolUse",permissionDecision:"allow",additionalContext:"Symbol lookup via grep detected. Prefer codebase-memory MCP (search_graph/trace_path) for symbol definitions/references/callers; if MCP is unavailable or the repo is not indexed, use `tk def|refs|callers <Symbol>` (lsp module). Text grep for `class X` misses cross-file references and interface dispatch."}}'
    exit 0
  fi
fi

exit 0
