#!/usr/bin/env bash
# tk-route.sh — PreToolUse hook: routes single dotnet/git commands through `tk`
# for compact output, and nudges symbol-grep toward `tk def|refs|callers`.
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

# Strip leading whitespace
trimmed="${cmd#"${cmd%%[![:space:]]*}"}"

# Never double-wrap tk commands
[[ "$trimmed" =~ ^tk[[:space:]] ]] && exit 0

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
    jq -n --arg c "$new" \
      '{hookSpecificOutput:{hookEventName:"PreToolUse",permissionDecision:"allow",updatedInput:{command:$c}}}'
    exit 0
  fi
fi

# NUDGE: suggest tk semantic nav for grep-for-symbol (allowed even when unsafe)
if [[ "$trimmed" =~ ^(grep|egrep|fgrep|rg)[[:space:]] ]] && \
   [[ "$cmd" =~ (class|record|interface|enum|struct)[[:space:]]+[A-Z] ]]; then
  jq -n \
    '{hookSpecificOutput:{hookEventName:"PreToolUse",permissionDecision:"allow",additionalContext:"Symbol lookup via grep detected. For where a symbol is defined / its references / its callers, prefer `tk def|refs|callers <Symbol>` — it resolves cross-file and crosses interface dispatch, unlike a text grep for `class X`/`record X`. (tk lsp module)"}}'
  exit 0
fi

exit 0
