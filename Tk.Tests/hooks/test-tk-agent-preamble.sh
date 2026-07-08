#!/usr/bin/env bash
# test-tk-agent-preamble.sh — case-matrix test for hooks/tk-agent-preamble.sh, the
# PreToolUse hook that appends a tk cheat-sheet to every Task-tool (subagent) prompt.
# Runs against the REAL hook script with the real `jq` on PATH.
#
# TAP-ish output: "ok - <description>" / "not ok - <description>" with a reason on
# failure. Exit code is 0 iff every case passed. Wired into `dotnet test` via
# Tk.Tests/hooks/HookAgentPreambleScriptTests.cs, which just shells out to it.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOOK="$SCRIPT_DIR/../../hooks/tk-agent-preamble.sh"
[[ -f "$HOOK" ]] || { echo "test-tk-agent-preamble: cannot find hook script at $HOOK" >&2; exit 1; }

REAL_JQ="$(command -v jq || true)"
[[ -n "$REAL_JQ" ]] || { echo "test-tk-agent-preamble: jq not found on PATH, cannot run" >&2; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

FAKE_BIN="$WORK/bin"
mkdir -p "$FAKE_BIN"
ln -s "$REAL_JQ" "$FAKE_BIN/jq"
for tool in cat bash sh; do
  p="$(command -v "$tool" || true)"
  [[ -n "$p" ]] && ln -sf "$p" "$FAKE_BIN/$tool"
done

PASS=0
FAIL=0

ok() {
  PASS=$((PASS + 1))
  echo "ok - $1"
}

fail() {
  FAIL=$((FAIL + 1))
  echo "not ok - $1"
  shift
  for line in "$@"; do
    echo "    $line"
  done
}

assert_eq() {
  local desc="$1" expected="$2" actual="$3"
  if [[ "$expected" == "$actual" ]]; then
    ok "$desc"
  else
    fail "$desc" "expected: [$expected]" "actual:   [$actual]"
  fi
}

assert_true() {
  local desc="$1" cond="$2"
  if [[ "$cond" == "1" ]]; then
    ok "$desc"
  else
    fail "$desc"
  fi
}

# hook_run_task <prompt>
# Runs the hook with tool_name=Task, tool_input.prompt=<prompt>. Sets OUT, RC, ERR.
hook_run_task() {
  local prompt="$1"
  local input
  input=$(jq -n --arg p "$prompt" '{tool_name:"Task", tool_input:{prompt:$p}}')
  OUT="$(PATH="$FAKE_BIN" bash "$HOOK" <<<"$input" 2>"$WORK/stderr")"
  RC=$?
  ERR="$(cat "$WORK/stderr")"
}

# hook_run_raw <json>
# Runs the hook with an arbitrary pre-built JSON payload on stdin. Sets OUT, RC, ERR.
hook_run_raw() {
  local input="$1"
  OUT="$(PATH="$FAKE_BIN" bash "$HOOK" <<<"$input" 2>"$WORK/stderr")"
  RC=$?
  ERR="$(cat "$WORK/stderr")"
}

echo "# hooks/tk-agent-preamble.sh case matrix"

# ── append case: a Task prompt with no marker gets the cheat-sheet appended ─────────────────
hook_run_task "Do the thing."
assert_eq "append: exit 0" "0" "$RC"
got_prompt="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.updatedInput.prompt // empty')"
assert_true "append: updatedInput.prompt present" "$([[ -n "$got_prompt" ]] && echo 1 || echo 0)"
assert_true "append: new prompt starts with the original prompt" \
  "$([[ "$got_prompt" == "Do the thing."* ]] && echo 1 || echo 0)"
assert_true "append: new prompt contains the cheat-sheet marker" \
  "$([[ "$got_prompt" == *"[tk cheat-sheet]"* ]] && echo 1 || echo 0)"
assert_true "append: new prompt mentions tk def/refs/callers" \
  "$([[ "$got_prompt" == *"tk def"* && "$got_prompt" == *"tk refs"* && "$got_prompt" == *"tk callers"* ]] && echo 1 || echo 0)"
assert_true "append: new prompt mentions the Read auto-cap threshold" \
  "$([[ "$got_prompt" == *"caps uncapped Reads of files over 500 lines"* ]] && echo 1 || echo 0)"
got_decision="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.permissionDecision // empty')"
assert_eq "append: permissionDecision" "allow" "$got_decision"

# ── marker idempotency: a prompt that already contains the marker is left untouched ─────────
hook_run_task "$(printf 'Do the thing.\n\n[tk cheat-sheet] already here, do not double-append')"
assert_eq "marker idempotency: exit 0" "0" "$RC"
assert_eq "marker idempotency: no stdout (prompt unchanged)" "" "$OUT"

# ── non-Task passthrough: any other tool_name is a silent no-op ─────────────────────────────
hook_run_raw '{"tool_name":"Bash","tool_input":{"command":"git status"}}'
assert_eq "non-Task passthrough: exit 0" "0" "$RC"
assert_eq "non-Task passthrough: no stdout" "" "$OUT"

# ── no prompt field: silent no-op ────────────────────────────────────────────────────────────
hook_run_raw '{"tool_name":"Task","tool_input":{}}'
assert_eq "no prompt field: exit 0" "0" "$RC"
assert_eq "no prompt field: no stdout" "" "$OUT"

# ── empty prompt: silent no-op ───────────────────────────────────────────────────────────────
hook_run_task ""
assert_eq "empty prompt: exit 0" "0" "$RC"
assert_eq "empty prompt: no stdout" "" "$OUT"

echo "# --- $PASS passed, $FAIL failed ---"
[[ "$FAIL" -eq 0 ]]
