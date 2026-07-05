#!/usr/bin/env bash
# test-tk-route.sh — case-matrix test for hooks/tk-route.sh, the production PreToolUse router
# that has had zero test coverage. Runs each case against the REAL hook script in a fully
# isolated environment: a temp HOME (so the decision log never touches the real, dated
# ~/.claude/tk-hook-YYYYMMDD.log) and a controlled PATH containing a FAKE `rtk` stub (recognizes a fixed
# command list, prints "rtk <cmd>" and exits 0; anything else exits 1) plus the real `jq` and
# the handful of core utils the hook script itself needs.
#
# TAP-ish output: "ok - <description>" / "not ok - <description>" with a reason on failure.
# Exit code is 0 iff every case passed. Wired into `dotnet test` via
# Tk.Tests/hooks/HookRouteScriptTests.cs, which just shells out to this script.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOOK="$SCRIPT_DIR/../../hooks/tk-route.sh"
[[ -f "$HOOK" ]] || { echo "test-tk-route: cannot find hook script at $HOOK" >&2; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

REAL_JQ="$(command -v jq || true)"
[[ -n "$REAL_JQ" ]] || { echo "test-tk-route: jq not found on PATH, cannot run" >&2; exit 1; }

# --- Fake PATH: real jq + core utils the hook needs, plus a fake rtk stub. Deliberately does
# NOT inherit the developer's real PATH, so a real `tk`/`rtk` on this machine can't leak in and
# mask what we're actually testing. ---
FAKE_BIN="$WORK/bin"
mkdir -p "$FAKE_BIN"
ln -s "$REAL_JQ" "$FAKE_BIN/jq"
for tool in cat date tr bash sh mkdir rm sed grep cut tail wc; do
  p="$(command -v "$tool" || true)"
  [[ -n "$p" ]] && ln -sf "$p" "$FAKE_BIN/$tool"
done

cat > "$FAKE_BIN/rtk" <<'STUB'
#!/usr/bin/env bash
# Fake rtk: recognizes a fixed command list for "rtk rewrite <cmd>" — including two exact
# grep commands (not a broad "grep " prefix) so the quote/escape-aware unsafe-detection
# proof cases below can reach this fallback, while OTHER grep commands stay unrecognized
# and fall through to the symbol-grep nudge check instead. Prints "rtk <cmd>" and exits 0
# when recognized, exits 1 (no output) otherwise.
if [[ "$1" != "rewrite" ]]; then
  exit 1
fi
cmd="$2"
case "$cmd" in
  "docker ps"|"docker ps -a"|"git commit"|"git commit -m test"| \
  'grep -rn "class Foo\|class Bar" src'|'grep -r class\|record src')
    printf 'rtk %s' "$cmd"
    exit 0
    ;;
  *)
    exit 1
    ;;
esac
STUB
chmod +x "$FAKE_BIN/rtk"

# --- PATH variant with no rtk at all (for the "rtk missing from PATH" case). ---
FAKE_BIN_NO_RTK="$WORK/bin-no-rtk"
mkdir -p "$FAKE_BIN_NO_RTK"
for f in "$FAKE_BIN"/*; do
  base="$(basename "$f")"
  [[ "$base" == "rtk" ]] && continue
  ln -sf "$f" "$FAKE_BIN_NO_RTK/$base"
done

# --- PATH variant with no jq at all (for the "jq absent" case). ---
FAKE_BIN_NO_JQ="$WORK/bin-no-jq"
mkdir -p "$FAKE_BIN_NO_JQ"
for f in "$FAKE_BIN"/*; do
  base="$(basename "$f")"
  [[ "$base" == "jq" ]] && continue
  ln -sf "$f" "$FAKE_BIN_NO_JQ/$base"
done

# The hook rotates its decision log daily via the real `date +%Y%m%d` (not faked here) —
# compute the same filename the hook itself computes, so log-content assertions below read
# the right file without needing to fake `date`.
TODAY="$(date +%Y%m%d)"
today_log_file() { printf '%s' "$FAKE_HOME/.claude/tk-hook-${TODAY}.log"; }

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

# Fresh FAKE_HOME per invocation so each case's decision-log check starts from a clean slate.
FAKE_HOME="$WORK/home"

# hook_run <path> <command> [cwd]
# Runs the hook with tool_name=Bash, tool_input.command=<command>, cwd=<cwd or /repo>, against
# the given PATH. Sets globals OUT, ERR, RC, LOG_CONTENT.
hook_run() {
  local path="$1" cmd="$2" cwd="${3:-/repo}"
  rm -rf "$FAKE_HOME"
  # Real Claude Code always has ~/.claude/ in place before a hook ever runs (it's where the
  # hook config itself lives) — pre-create it so the isolated HOME matches that reality rather
  # than exercising the unrelated "no ~/.claude at all" edge case.
  mkdir -p "$FAKE_HOME/.claude"

  local input
  input=$(jq -n --arg c "$cmd" --arg cwd "$cwd" \
    '{tool_name:"Bash", tool_input:{command:$c}, cwd:$cwd}')

  OUT="$(PATH="$path" HOME="$FAKE_HOME" bash "$HOOK" <<<"$input" 2>"$WORK/stderr")"
  RC=$?
  ERR="$(cat "$WORK/stderr")"
  if [[ -f "$(today_log_file)" ]]; then
    LOG_CONTENT="$(cat "$(today_log_file)")"
  else
    LOG_CONTENT=""
  fi
}

# expect_rewrite <desc> <command> <expected_new_command> <expected_decision> [cwd]
expect_rewrite() {
  local desc="$1" cmd="$2" expected_new="$3" decision="$4" cwd="${5:-/repo}"
  hook_run "$FAKE_BIN" "$cmd" "$cwd"

  assert_eq "$desc: exit 0" "0" "$RC"
  local got_new
  got_new="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.updatedInput.command // empty')"
  assert_eq "$desc: updatedInput.command" "$expected_new" "$got_new"
  local got_decision
  got_decision="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.permissionDecision // empty')"
  assert_eq "$desc: permissionDecision" "allow" "$got_decision"
  assert_true "$desc: decision-log line present" "$([[ "$LOG_CONTENT" == *$'\t'"$decision"$'\t'* ]] && echo 1 || echo 0)"
  assert_true "$desc: decision-log carries original cmd" "$([[ "$LOG_CONTENT" == *"$cmd"* ]] && echo 1 || echo 0)"
}

# expect_passthrough <desc> <command> [cwd] [path]
expect_passthrough() {
  local desc="$1" cmd="$2" cwd="${3:-/repo}" path="${4:-$FAKE_BIN}"
  hook_run "$path" "$cmd" "$cwd"

  assert_eq "$desc: exit 0" "0" "$RC"
  assert_eq "$desc: no stdout (pass-through, command unchanged)" "" "$OUT"
  assert_eq "$desc: no decision-log entry" "" "$LOG_CONTENT"
}

# hook_run_keep_home <path> <command> [cwd]
# Same as hook_run, but does NOT reset FAKE_HOME first — needed to prove the nudge
# throttle, which reads the previous "nudge" decision-log timestamp across invocations.
hook_run_keep_home() {
  local path="$1" cmd="$2" cwd="${3:-/repo}"
  mkdir -p "$FAKE_HOME/.claude"

  local input
  input=$(jq -n --arg c "$cmd" --arg cwd "$cwd" \
    '{tool_name:"Bash", tool_input:{command:$c}, cwd:$cwd}')

  OUT="$(PATH="$path" HOME="$FAKE_HOME" bash "$HOOK" <<<"$input" 2>"$WORK/stderr")"
  RC=$?
  ERR="$(cat "$WORK/stderr")"
  if [[ -f "$(today_log_file)" ]]; then
    LOG_CONTENT="$(cat "$(today_log_file)")"
  else
    LOG_CONTENT=""
  fi
}

# expect_nudge <desc> <command> [cwd]
# Uses hook_run_keep_home so callers can chain a throttle case right after.
expect_nudge() {
  local desc="$1" cmd="$2" cwd="${3:-/repo}"
  hook_run_keep_home "$FAKE_BIN" "$cmd" "$cwd"

  assert_eq "$desc: exit 0" "0" "$RC"
  local got_ctx
  got_ctx="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.additionalContext // empty')"
  assert_true "$desc: additionalContext present" "$([[ -n "$got_ctx" ]] && echo 1 || echo 0)"
  local got_updated
  got_updated="$(printf '%s' "$OUT" | jq -r 'if (.hookSpecificOutput | has("updatedInput")) then "present" else "" end')"
  assert_eq "$desc: no updatedInput (command unchanged)" "" "$got_updated"
  assert_true "$desc: decision-log line present" "$([[ "$LOG_CONTENT" == *$'\t'"nudge"$'\t'* ]] && echo 1 || echo 0)"
}

# expect_no_nudge <desc> <command> [cwd]
# Like expect_nudge's negative: no stdout at all, no new decision-log entry. Uses
# hook_run_keep_home so it composes with the throttle test (which needs shared state).
expect_no_nudge() {
  local desc="$1" cmd="$2" cwd="${3:-/repo}"
  local before_log="$LOG_CONTENT"
  hook_run_keep_home "$FAKE_BIN" "$cmd" "$cwd"

  assert_eq "$desc: exit 0" "0" "$RC"
  assert_eq "$desc: no stdout" "" "$OUT"
  assert_eq "$desc: no new decision-log entry" "$before_log" "$LOG_CONTENT"
}

echo "# hooks/tk-route.sh case matrix"

# ── tk-routed commands ──────────────────────────────────────────────────────
expect_rewrite "dotnet build -> tk" "dotnet build" "tk dotnet build" "route-tk"
expect_rewrite "dotnet test -> tk" "dotnet test" "tk dotnet test" "route-tk"
expect_rewrite "dotnet restore -> tk" "dotnet restore" "tk dotnet restore" "route-tk"
expect_rewrite "git status -> tk" "git status" "tk git status" "route-tk"
expect_rewrite "git log -> tk" "git log" "tk git log" "route-tk"

# ── rtk-routed commands (fake rtk recognizes these) ─────────────────────────
expect_rewrite "git commit -> rtk" "git commit -m test" "rtk git commit -m test" "route-rtk"
expect_rewrite "docker ps -> rtk" "docker ps" "rtk docker ps" "route-rtk"

# ── git diff/show: deliberately excluded from both tk and rtk routing ───────
expect_passthrough "git diff -> pass, no log" "git diff"
expect_passthrough "git show HEAD -> pass, no log" "git show HEAD"

# ── already wrapped: never double-wrap ───────────────────────────────────────
expect_passthrough "already tk -> pass, no log" "tk git status"
expect_passthrough "already rtk -> pass, no log" "rtk rewrite git status"

# ── compound commands: unsafe, left untouched ───────────────────────────────
expect_passthrough "pipe -> pass (unsafe)" "dotnet build | cat"
expect_passthrough "&& chain -> pass (unsafe)" "git status && echo done"
expect_passthrough "; separator -> pass (unsafe)" "git status; echo done"
expect_passthrough 'subshell $(...) -> pass (unsafe)' 'echo $(git status)'
expect_passthrough "backtick substitution -> pass (unsafe)" 'echo `git status`'
expect_passthrough "trailing background & -> pass (unsafe)" "git status &"
hook_run "$FAKE_BIN" "$(printf 'git status\necho done')"
assert_eq "embedded newline -> pass (unsafe): exit 0" "0" "$RC"
assert_eq "embedded newline -> pass (unsafe): no stdout" "" "$OUT"
assert_eq "embedded newline -> pass (unsafe): no decision-log entry" "" "$LOG_CONTENT"

# ── quoted / escaped metacharacters: the unsafe-detection runs against a copy with
# backslash-escapes and quoted segments stripped, so metacharacters that are literal text
# to the shell (inside quotes, or backslash-escaped) no longer false-flag a command as
# unsafe. These cases prove the fix end-to-end rather than just pinning the old gap. ───────

# (a) quoted "|" inside a grep pattern is literal to the shell -> safe single command ->
# reaches the rtk fallback (fake rtk recognizes this exact grep command).
expect_rewrite 'quoted pipe in grep pattern -> safe, routed to rtk' \
  'grep -rn "class Foo\|class Bar" src' \
  'rtk grep -rn "class Foo\|class Bar" src' "route-rtk"

# (b) quoted "&&" inside a git log --grep value is literal -> safe single command -> still
# matches the git-log -> tk route.
expect_rewrite 'quoted "&&" in git log --grep -> safe, routed to tk' \
  'git log --grep="fix && cleanup"' \
  'tk git log --grep="fix && cleanup"' "route-tk"

# (c) unbalanced quote: stripping can't reason past a lone survivor quote -> unsafe.
expect_passthrough 'unbalanced quote -> pass (unsafe)' 'echo "a && b'

# (d) a real, unquoted pipe is still unsafe -- even in front of an otherwise-routable
# git command.
expect_passthrough 'unquoted pipe -> pass (unsafe)' 'git status | head -5'

# (e) backslash-escaped pipe outside quotes: the shell sees a literal "|", so this is a
# safe single command -> reaches the rtk fallback, same as (a).
expect_rewrite 'backslash-escaped pipe -> safe, routed to rtk' \
  'grep -r class\|record src' \
  'rtk grep -r class\|record src' "route-rtk"

# ── redirects: NOT unsafe; a single redirected command still gets the tk prefix ────────────
expect_rewrite "redirect -> still routed to tk" "git status > /tmp/tk-route-test-out.txt" \
  "tk git status > /tmp/tk-route-test-out.txt" "route-tk"

# ── git -C / git -c: a leading run of `-C <path>` / `-c <key=val>` global flags before the
# subcommand is recognized and routed just like the bare form. ─────────────────────────────
expect_rewrite "git -C <dir> status -> tk" \
  "git -C /some/other/repo status" "tk git -C /some/other/repo status" "route-tk"
expect_rewrite "git -c k=v status -> tk" \
  "git -c core.pager=cat status" "tk git -c core.pager=cat status" "route-tk"
expect_rewrite "git -c k=v -C <dir> log --oneline -> tk (combo)" \
  "git -c core.pager=cat -C /x/y log --oneline" \
  "tk git -c core.pager=cat -C /x/y log --oneline" "route-tk"
expect_rewrite 'git -C "<quoted path with spaces>" status -> tk' \
  'git -C "/some path/with spaces" status' \
  'tk git -C "/some path/with spaces" status' "route-tk"
expect_rewrite "git -C <dir> log -5 -> tk" \
  "git -C /some/other/repo log -5" "tk git -C /some/other/repo log -5" "route-tk"

# git -C/-c diff|show: still excluded, same reasoning as bare diff/show.
expect_passthrough "git -C <dir> diff -> pass (excluded, same as bare diff)" \
  "git -C /some/other/repo diff"
expect_passthrough "git -C <dir> show HEAD -> pass (excluded, same as bare show)" \
  "git -C /some/other/repo show HEAD"

# ── rtk unavailable ──────────────────────────────────────────────────────────
expect_passthrough "rtk missing from PATH -> pass" "docker ps" "/repo" "$FAKE_BIN_NO_RTK"

# ── rtk present but doesn't recognize the command (fake exits 1) ────────────
expect_passthrough "rtk rewrite exit 1 -> pass" "kubectl get pods"

# ── jq absent: silent no-op, exit 0, no stdout, no stderr noise ─────────────
rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"
input=$(jq -n --arg c "git status" --arg cwd "/repo" '{tool_name:"Bash", tool_input:{command:$c}, cwd:$cwd}')
OUT="$(PATH="$FAKE_BIN_NO_JQ" HOME="$FAKE_HOME" bash "$HOOK" <<<"$input" 2>"$WORK/stderr")"
RC=$?
ERR="$(cat "$WORK/stderr")"
assert_eq "jq absent: exit 0" "0" "$RC"
assert_eq "jq absent: no stdout" "" "$OUT"
assert_eq "jq absent: no stderr noise" "" "$ERR"

# ── symbol-grep nudge: fires only after tk/rtk rewrite has declined, regardless of the
# unsafe flag, and is throttled to once per 300s (by decision-log timestamp). Each group
# below starts from a clean FAKE_HOME so it isn't throttled by an earlier group's log. ────

# Group 1: unsafe path still nudges, and a second symbol-grep right after it is throttled.
rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""

# Symbol grep inside a pipe: unsafe (real unquoted pipe), so it never reaches the
# rewrite/rtk-fallback path at all -- but the nudge check runs regardless of the unsafe
# flag, so it still fires.
expect_nudge 'symbol grep inside a pipe -> still nudged' \
  'grep -rn "class Gamma\|class Delta" other_src | head -3'

# Second symbol grep run immediately after -> throttled (last nudge logged <300s ago).
expect_no_nudge 'second symbol grep immediately after -> throttled, not nudged' \
  'grep -rn "class Epsilon\|class Zeta" other_src'

# Group 2: safe path nudges too, and a non-symbol-shaped grep never nudges at all.
rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""

# Symbol grep via quoted alternation ("class Foo\|class Bar" shape, not one of the fake
# rtk's two reserved exact-match grep commands) -> safe, unrecognized by rtk -> nudge.
expect_nudge 'symbol grep (quoted alternation) -> nudge' \
  'grep -rn "class Alpha\|class Beta" other_src'

# Plain free-text grep, no symbol shape -> no nudge (and no rewrite either).
expect_no_nudge 'plain free-text grep -> no nudge' 'grep -rn "TODO" src'

# ── daily log rotation: decisions land in a dated file (tk-hook-YYYYMMDD.log), never the
# old flat tk-hook.log. ──────────────────────────────────────────────────────────────────────
rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""
hook_run "$FAKE_BIN" "git status"
assert_true "log rotation: computed dated log filename matches YYYYMMDD pattern" \
  "$([[ "$(today_log_file)" =~ tk-hook-[0-9]{8}\.log$ ]] && echo 1 || echo 0)"
assert_true "log rotation: dated log file was created" \
  "$([[ -f "$(today_log_file)" ]] && echo 1 || echo 0)"
assert_true "log rotation: dated log file contains the route-tk line" \
  "$([[ "$LOG_CONTENT" == *$'\t'"route-tk"$'\t'* ]] && echo 1 || echo 0)"
assert_true "log rotation: legacy flat tk-hook.log is NOT created" \
  "$([[ ! -f "$FAKE_HOME/.claude/tk-hook.log" ]] && echo 1 || echo 0)"

# ── Read-tool nudge: large whole-file reads (no offset/limit, >400 lines) get an
# additionalContext hint; silent for small files, missing files, or when offset/limit is
# given; throttled like the symbol-grep nudge but independently (own "nudge-read" decision,
# own 300s window, does not suppress or get suppressed by the symbol-grep "nudge"). ─────────
BIG_FILE="$WORK/big_file.txt"
seq 1 500 > "$BIG_FILE"
SMALL_FILE="$WORK/small_file.txt"
seq 1 10 > "$SMALL_FILE"
MISSING_FILE="$WORK/does_not_exist.txt"

# hook_run_read <path> <file> <offset> <limit> [cwd]
# offset/limit empty string means "absent from tool_input" (not merely null).
hook_run_read() {
  local path="$1" file="$2" offset="$3" limit="$4" cwd="${5:-/repo}"
  rm -rf "$FAKE_HOME"
  mkdir -p "$FAKE_HOME/.claude"
  hook_run_read_keep_home "$path" "$file" "$offset" "$limit" "$cwd"
}

# hook_run_read_keep_home <path> <file> <offset> <limit> [cwd]
# Same as hook_run_read but does not reset FAKE_HOME first, so the nudge-read throttle
# (which reads the previous "nudge-read" decision-log timestamp) can be exercised across
# invocations, mirroring hook_run_keep_home for the symbol-grep nudge.
hook_run_read_keep_home() {
  local path="$1" file="$2" offset="$3" limit="$4" cwd="${5:-/repo}"
  mkdir -p "$FAKE_HOME/.claude"

  local input
  input=$(jq -n --arg f "$file" --arg cwd "$cwd" --arg o "$offset" --arg l "$limit" '
    {tool_name:"Read", cwd:$cwd,
     tool_input: ({file_path:$f}
       + (if $o == "" then {} else {offset:($o|tonumber)} end)
       + (if $l == "" then {} else {limit:($l|tonumber)} end))}')

  OUT="$(PATH="$path" HOME="$FAKE_HOME" bash "$HOOK" <<<"$input" 2>"$WORK/stderr")"
  RC=$?
  ERR="$(cat "$WORK/stderr")"
  if [[ -f "$(today_log_file)" ]]; then
    LOG_CONTENT="$(cat "$(today_log_file)")"
  else
    LOG_CONTENT=""
  fi
}

# expect_read_nudge <desc> <file> <offset> <limit> [cwd]
expect_read_nudge() {
  local desc="$1" file="$2" offset="$3" limit="$4" cwd="${5:-/repo}"
  hook_run_read_keep_home "$FAKE_BIN" "$file" "$offset" "$limit" "$cwd"

  assert_eq "$desc: exit 0" "0" "$RC"
  local got_ctx
  got_ctx="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.additionalContext // empty')"
  assert_true "$desc: additionalContext present" "$([[ -n "$got_ctx" ]] && echo 1 || echo 0)"
  local got_updated
  got_updated="$(printf '%s' "$OUT" | jq -r 'if (.hookSpecificOutput | has("updatedInput")) then "present" else "" end')"
  assert_eq "$desc: no updatedInput (Read input unchanged)" "" "$got_updated"
  assert_true "$desc: decision-log line present (nudge-read)" \
    "$([[ "$LOG_CONTENT" == *$'\t'"nudge-read"$'\t'* ]] && echo 1 || echo 0)"
  assert_true "$desc: decision-log carries file path" \
    "$([[ "$LOG_CONTENT" == *"$file"* ]] && echo 1 || echo 0)"
}

# expect_no_read_nudge <desc> <file> <offset> <limit> [cwd]
expect_no_read_nudge() {
  local desc="$1" file="$2" offset="$3" limit="$4" cwd="${5:-/repo}"
  local before_log="$LOG_CONTENT"
  hook_run_read_keep_home "$FAKE_BIN" "$file" "$offset" "$limit" "$cwd"

  assert_eq "$desc: exit 0" "0" "$RC"
  assert_eq "$desc: no stdout" "" "$OUT"
  assert_eq "$desc: no new decision-log entry" "$before_log" "$LOG_CONTENT"
}

# Group 1: fires on a big file with no offset/limit, then throttled on an immediate repeat.
rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""
expect_read_nudge "big file, no offset/limit -> nudge" "$BIG_FILE" "" ""
expect_no_read_nudge "big file again immediately after -> throttled, not nudged" "$BIG_FILE" "" ""

# Group 2: silent cases, all on a fresh home so none are throttled by a prior nudge.
rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""
expect_no_read_nudge "big file with offset given -> no nudge" "$BIG_FILE" "100" ""
expect_no_read_nudge "big file with limit given -> no nudge" "$BIG_FILE" "" "50"
expect_no_read_nudge "big file with offset and limit given -> no nudge" "$BIG_FILE" "10" "50"
expect_no_read_nudge "small file (<=400 lines), no offset/limit -> no nudge" "$SMALL_FILE" "" ""
expect_no_read_nudge "missing file -> no nudge" "$MISSING_FILE" "" ""

# Group 3: the read-nudge throttle is independent of the symbol-grep nudge throttle in both
# directions -- neither one's recent firing suppresses the other.
rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""
expect_nudge 'symbol grep -> nudge (before independence check)' \
  'grep -rn "class Nine\|class Ten" other_src'
expect_read_nudge "big file read right after a symbol-grep nudge -> still nudges (independent throttle)" \
  "$BIG_FILE" "" ""

rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""
expect_read_nudge "big file read -> nudge (before independence check, reverse direction)" \
  "$BIG_FILE" "" ""
expect_nudge 'symbol grep right after a read nudge -> still nudges (independent throttle)' \
  'grep -rn "class Eleven\|class Twelve" other_src'

echo "# --- $PASS passed, $FAIL failed ---"
[[ "$FAIL" -eq 0 ]]
