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

tool=$(printf '%s' "$INPUT" | jq -r '.tool_name // empty')
[[ "$tool" != "Bash" ]] && exit 0

cmd=$(printf '%s' "$INPUT" | jq -r '.tool_input.command // empty')
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
unsafe=0
case "$cmd" in
  *\|*) unsafe=1 ;;       # pipe (also covers ||)
  *\;*) unsafe=1 ;;       # command separator
  *\`*) unsafe=1 ;;       # backtick command substitution
  *'$('*) unsafe=1 ;;     # $() command substitution
  *'&&'*) unsafe=1 ;;     # logical AND chain
esac
[[ "$cmd" =~ \&[[:space:]]*$ ]] && unsafe=1   # trailing background &
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

# NUDGE: suggest tk semantic nav for grep-for-symbol — disabled (codebase-memory-mcp handles symbol nav)
# if [[ "$trimmed" =~ ^(grep|egrep|fgrep|rg)[[:space:]] ]] && \
#    [[ "$cmd" =~ (class|record|interface|enum|struct)[[:space:]]+[A-Z] ]]; then
#   _log_decision "nudge" "$cmd"
#   jq -n \
#     '{hookSpecificOutput:{hookEventName:"PreToolUse",permissionDecision:"allow",additionalContext:"Symbol lookup via grep detected. For where a symbol is defined / its references / its callers, prefer `tk def|refs|callers <Symbol>` — it resolves cross-file and crosses interface dispatch, unlike a text grep for `class X`/`record X`. (tk lsp module)"}}'
#   exit 0
# fi

exit 0
