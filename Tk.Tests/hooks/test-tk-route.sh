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
for tool in cat date tr bash sh mkdir rm sed grep cut tail wc awk; do
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
# Mirrors real rtk: cat/head/tail <path>.cs -> "rtk read <path>.cs" (real rtk does NOT
# recognize sed) -- needed so the .cs stealth-read nudge tests below reproduce the real
# ordering, where the rewrite fires and the nudge must still be attached to it.
if [[ "$cmd" =~ ^(cat|head|tail)[[:space:]]+([^[:space:]]+\.cs)$ ]]; then
  printf 'rtk read %s' "${BASH_REMATCH[2]}"
  exit 0
fi
case "$cmd" in
  "FOO=bar find . -type f -o -type d")
    printf 'FOO=bar rtk find . -type f -o -type d'; exit 0 ;;
  "command find . -type f -o -type d")
    printf 'command rtk find . -type f -o -type d'; exit 0 ;;
  "env FOO=bar find . -type f -o -type d")
    printf 'env FOO=bar rtk find . -type f -o -type d'; exit 0 ;;
  "sudo find . -type f -o -type d")
    printf 'sudo rtk find . -type f -o -type d'; exit 0 ;;
  "command grep -n foo file.txt")
    printf 'command rtk grep -n foo file.txt'; exit 0 ;;
  "env grep -n foo file.txt")
    printf 'env rtk grep -n foo file.txt'; exit 0 ;;
  "sudo grep -n foo file.txt")
    printf 'sudo rtk grep -n foo file.txt'; exit 0 ;;
esac
case "$cmd" in
  "docker ps"|"docker ps -a"|"git commit"|"git commit -m test"| \
  "find . -type f"|"find . -type f -o -type d"| \
  "FOO=bar find . -type f -o -type d"| \
  "find . -not -path ./obj"|"find . ! -path ./obj"| \
  "find . -type f -exec wc -l {} \\;"|"find . -type f -delete"| \
  "find . -type f -ls"|"find . -type f -print0"| \
  "find . \( -type f -a -name '*.cs' \)"| \
  'grep -rn "class Foo\|class Bar" src'|'grep -r class\|record src'| \
  'grep -n "^+.*" file.txt'|'rg foo bar')
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

# hook_run_keep_home_sid <path> <command> <session_id> [cwd]
# Same as hook_run_keep_home, but stamps a session_id on the hook input — needed to prove
# the nudge throttle is keyed by session_id (a nudge in one session must not throttle a
# nudge in a different session).
hook_run_keep_home_sid() {
  local path="$1" cmd="$2" sid="$3" cwd="${4:-/repo}"
  mkdir -p "$FAKE_HOME/.claude"

  local input
  input=$(jq -n --arg c "$cmd" --arg cwd "$cwd" --arg sid "$sid" \
    '{tool_name:"Bash", tool_input:{command:$c}, cwd:$cwd, session_id:$sid}')

  OUT="$(PATH="$path" HOME="$FAKE_HOME" bash "$HOOK" <<<"$input" 2>"$WORK/stderr")"
  RC=$?
  ERR="$(cat "$WORK/stderr")"
  if [[ -f "$(today_log_file)" ]]; then
    LOG_CONTENT="$(cat "$(today_log_file)")"
  else
    LOG_CONTENT=""
  fi
}

# expect_nudge_sid <desc> <command> <session_id> [cwd]
expect_nudge_sid() {
  local desc="$1" cmd="$2" sid="$3" cwd="${4:-/repo}"
  hook_run_keep_home_sid "$FAKE_BIN" "$cmd" "$sid" "$cwd"

  assert_eq "$desc: exit 0" "0" "$RC"
  local got_ctx
  got_ctx="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.additionalContext // empty')"
  assert_true "$desc: additionalContext present" "$([[ -n "$got_ctx" ]] && echo 1 || echo 0)"
  assert_true "$desc: decision-log line present" "$([[ "$LOG_CONTENT" == *$'\t'"nudge"$'\t'* ]] && echo 1 || echo 0)"
}

# expect_no_nudge_sid <desc> <command> <session_id> [cwd]
expect_no_nudge_sid() {
  local desc="$1" cmd="$2" sid="$3" cwd="${4:-/repo}"
  local before_log="$LOG_CONTENT"
  hook_run_keep_home_sid "$FAKE_BIN" "$cmd" "$sid" "$cwd"

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
expect_rewrite "simple find -> rtk" "find . -type f" "rtk find . -type f" "route-rtk"
expect_passthrough "find with -o stays native" "find . -type f -o -type d"
expect_passthrough "env-prefixed find with -o stays native" \
  "FOO=bar find . -type f -o -type d"
expect_passthrough "command-wrapped find with -o stays native" \
  "command find . -type f -o -type d"
expect_passthrough "env-wrapped find with -o stays native" \
  "env FOO=bar find . -type f -o -type d"
expect_passthrough "sudo-wrapped find with -o stays native" \
  "sudo find . -type f -o -type d"
expect_passthrough "find with -not stays native" "find . -not -path ./obj"
expect_passthrough "find with ! stays native" "find . ! -path ./obj"
expect_passthrough "find with explicit -a/parentheses stays native" \
  "find . \( -type f -a -name '*.cs' \)"
expect_passthrough "find with -exec stays native" \
  "find . -type f -exec wc -l {} \;"
expect_passthrough "find with -delete stays native" "find . -type f -delete"
expect_passthrough "find with -ls stays native" "find . -type f -ls"
expect_passthrough "find with -print0 stays native" "find . -type f -print0"

# ── git diff/show: deliberately excluded from both tk and rtk routing ───────
expect_passthrough "git diff -> pass, no log" "git diff"
expect_passthrough "git show HEAD -> pass, no log" "git show HEAD"

# ── already wrapped: never double-wrap ───────────────────────────────────────
expect_passthrough "already tk -> pass, no log" "tk git status"
expect_passthrough "already rtk -> pass, no log" "rtk rewrite git status"

# ── compound commands: quote-aware segment rewrites, native helpers preserved ─────────────
expect_rewrite "pipe -> route supported left segment only" \
  "dotnet build | cat" "tk dotnet build | cat" "route-tk"
expect_rewrite "&& chain -> route supported segment only" \
  "git status && echo done" "tk git status && echo done" "route-tk"
expect_rewrite "; separator -> route supported segment only" \
  "git status; echo done" "tk git status; echo done" "route-tk"
expect_rewrite "cd chain and pipe -> preserve cd/head, route dotnet build" \
  "cd x && dotnet build | head -50" "cd x && tk dotnet build | head -50" "route-tk"
expect_rewrite "pipeline and later chain -> route both supported git segments" \
  "git log | head -5 && git status" "tk git log | head -5 && tk git status" "route-tk"
expect_rewrite "quoted operator text in compound -> do not split inside quotes" \
  'echo "git status && no split" && git status' \
  'echo "git status && no split" && tk git status' "route-tk"
expect_rewrite "env prefix before supported command in compound -> route (tk inserted after prefix)" \
  "FOO=bar dotnet build && echo done" "FOO=bar tk dotnet build && echo done" "route-tk"
expect_rewrite "multiple env prefixes -> route (tk inserted after all prefixes)" \
  "FOO=bar BAZ=qux dotnet build" "FOO=bar BAZ=qux tk dotnet build" "route-tk"
expect_passthrough "git diff in compound -> pass (excluded, same as simple diff)" \
  "git diff | head -20"
expect_rewrite "already tk segment in compound -> do not double-wrap, route later segment" \
  "tk git status && git status" "tk git status && tk git status" "route-tk"
expect_passthrough 'subshell $(...) -> pass (unsafe)' 'echo $(git status)'
expect_passthrough "backtick substitution -> pass (unsafe)" 'echo `git status`'
expect_passthrough "trailing background & -> pass (unsafe)" "git status &"

# ── quoted / escaped metacharacters: the unsafe-detection runs against a copy with
# backslash-escapes and quoted segments stripped, so metacharacters that are literal text
# to the shell (inside quotes, or backslash-escaped) no longer false-flag a command as
# unsafe. These cases prove the fix end-to-end rather than just pinning the old gap. ───────

# (a) quoted "|" inside a grep pattern is literal to the shell -> safe single command, but
# grep-family commands are excluded from the rtk fallback (GREP_FAMILY_RE) regardless of
# allow_rtk -> command stays byte-for-byte unchanged, even though the fake rtk stub DOES
# recognize this exact command (proves the exclusion actually fires, not just an accidental
# miss). Symbol-shaped ("class Foo"/"class Bar") -> still gets the symbol-grep nudge.
expect_nudge 'quoted pipe in grep pattern -> passthrough (grep excluded from rtk), still nudged' \
  'grep -rn "class Foo\|class Bar" src'

# (b) quoted "&&" inside a git log --grep value is literal -> safe single command -> still
# matches the git-log -> tk route.
expect_rewrite 'quoted "&&" in git log --grep -> safe, routed to tk' \
  'git log --grep="fix && cleanup"' \
  'tk git log --grep="fix && cleanup"' "route-tk"

# (c) unbalanced quote: stripping can't reason past a lone survivor quote -> unsafe.
expect_passthrough 'unbalanced quote -> pass (unsafe)' 'echo "a && b'

# (d) a real, unquoted pipe is a compound operator. The supported git segment is routed
# and the native head segment is preserved.
expect_rewrite 'unquoted pipe -> route supported left segment only' \
  'git status | head -5' 'tk git status | head -5' "route-tk"

# (e) backslash-escaped pipe outside quotes: the shell sees a literal "|", so this is a safe
# single command, but grep-family stays excluded from the rtk fallback -> passthrough
# unchanged, even though the fake rtk stub recognizes this exact command too. Not
# symbol-shaped (no uppercase-led identifier) -> no nudge either, pure silent passthrough.
expect_passthrough 'backslash-escaped pipe -> passthrough (grep excluded from rtk)' \
  'grep -r class\|record src'

# ── grep-family commands never lose their executable/regex dialect to the rtk fallback ─────
# (GREP_FAMILY_RE, hooks/tk-route.sh). Not symbol-shaped -> no nudge either, pure silent
# passthrough; the fake rtk stub is unrecognizing-by-default here (would exit 1 anyway for
# the two new patterns), but the "^+.*" case is ALSO hardcoded into the stub's recognized
# list above, so this proves the exclusion actually fires rather than merely rtk declining.
expect_passthrough "grep with regex anchor pattern -> passthrough (grep excluded from rtk)" \
  'grep -n "^+.*" file.txt'
expect_passthrough "grep with escaped-plus pattern -> passthrough (grep excluded from rtk)" \
  'grep -n "^\+.*" file.txt'
expect_passthrough "rg (ripgrep) -> passthrough (grep-family excluded from rtk)" \
  'rg foo bar'
expect_passthrough "command-wrapped grep -> passthrough (grep-family rewrite rejected)" \
  'command grep -n foo file.txt'
expect_passthrough "env-wrapped grep -> passthrough (grep-family rewrite rejected)" \
  'env grep -n foo file.txt'
expect_passthrough "sudo-wrapped grep -> passthrough (grep-family rewrite rejected)" \
  'sudo grep -n foo file.txt'

# ugrep counts as grep-family too, for both the rtk-fallback exclusion and the symbol-grep
# nudge (the nudge regex previously omitted ugrep -- fixed alongside the exclusion above).
expect_passthrough "ugrep -> passthrough (grep-family excluded from rtk)" \
  'ugrep -n foo bar.txt'
rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""
expect_nudge 'symbol-shaped ugrep -> nudge (ugrep now included in grep-family)' \
  'ugrep -rn "class Foo\|class Bar" src'

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

# ── || chains, mixed operators, quoted operator text as a literal string ────────────────────
expect_rewrite "|| chain -> route supported segment only" \
  "git status || echo failed" "tk git status || echo failed" "route-tk"
expect_rewrite "git diff && git status -> diff stays raw, status routed" \
  "git diff && git status" "git diff && tk git status" "route-tk"
expect_rewrite 'quoted "&&" and "||" as a literal string argument -> not split, single command routed' \
  'echo "a && b || c" && git status' \
  'echo "a && b || c" && tk git status' "route-tk"

# ── command substitution: both $(...) and backticks passthrough the WHOLE command ───────────
expect_passthrough 'command substitution $(...) around a routable command -> pass (unsafe)' \
  'git log && echo $(git status)'
expect_passthrough "backtick substitution around a routable command -> pass (unsafe)" \
  'git log && echo `git status`'

# ── newline as a segment separator: bare unquoted newlines split like `;` ───────────────────
hook_run "$FAKE_BIN" "$(printf 'git status\ndotnet build')"
assert_eq "newline splitting -> exit 0" "0" "$RC"
got_new="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.updatedInput.command // empty')"
assert_eq "newline splitting -> both segments routed" "$(printf 'tk git status\ntk dotnet build')" "$got_new"
assert_true "newline splitting -> decision-log line present" \
  "$([[ "$LOG_CONTENT" == *$'\t'"route-tk"$'\t'* ]] && echo 1 || echo 0)"

# ── heredoc / backslash-newline continuation / control-flow keywords: conservative full
# passthrough, even though a routable command appears inside. ───────────────────────────────
expect_passthrough "heredoc -> pass (unsafe)" "$(printf 'cat <<EOF\ngit status\nEOF')"
expect_passthrough "backslash-newline continuation -> pass (unsafe)" "$(printf 'git status &&\\\n  echo done')"
expect_passthrough "if/then/fi control flow -> pass (unsafe)" \
  "$(printf 'if true; then git status; fi')"
expect_passthrough "for loop control flow -> pass (unsafe)" \
  "$(printf 'for f in a b; do git status; done')"

# ── three-tier downstream guard for pipelines ────────────────────────────────────────────────
# Tier 1: every downstream consumer is a display modifier (head/tail/cat) -> LHS routed, rest
# of the pipeline preserved verbatim. (dotnet build | cat and git status | head -5 above already
# cover this; one more shape here for a routed dotnet test.)
expect_rewrite "tier 1: dotnet test | head -> route LHS only" \
  "dotnet test | head -50" "tk dotnet test | head -50" "route-tk"

# Tier 2: dotnet test filtered by a problem-shaped grep, then head -> WHOLE pipeline replaced
# with `tk dotnet test ...`, filters dropped, additionalContext hint attached.
hook_run "$FAKE_BIN" 'dotnet test 2>&1 | grep -E "Passed|Failed" | head -20'
assert_eq "tier 2: dotnet test | grep problem-pattern | head -> exit 0" "0" "$RC"
got_new="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.updatedInput.command // empty')"
assert_eq "tier 2: whole pipeline replaced with tk dotnet test" "tk dotnet test 2>&1" "$got_new"
got_ctx="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.additionalContext // empty')"
assert_true "tier 2: additionalContext hint present" "$([[ -n "$got_ctx" ]] && echo 1 || echo 0)"
assert_true "tier 2: decision-log line present" \
  "$([[ "$LOG_CONTENT" == *$'\t'"route-tk"$'\t'* ]] && echo 1 || echo 0)"

# Tier 2 rejection: grep has -v (inverted match, changes output shape) -> whole pipeline
# passthrough, not even the LHS gets routed.
expect_passthrough "tier 2 rejection: grep -v on dotnet test -> pass (whole pipeline untouched)" \
  'dotnet test | grep -v FAIL'
# Tier 2 rejection: grep pattern is not problem-shaped -> whole pipeline passthrough.
expect_passthrough "tier 2 rejection: non-problem grep pattern -> pass (whole pipeline untouched)" \
  'dotnet test | grep TODO'

# Tier 3: any other content filter downstream -> whole pipeline passthrough, including the
# routable LHS.
expect_passthrough "tier 3: dotnet test | wc -l -> pass (whole pipeline untouched)" \
  "dotnet test | wc -l"
expect_passthrough "tier 3: git log | grep fix -> pass (whole pipeline untouched)" \
  "git log | grep fix"

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

# Group 3: the nudge throttle is keyed by session_id -- a nudge in one session must not
# throttle a nudge in a DIFFERENT session (e.g. a freshly spawned subagent), but a second
# nudge in the SAME session right after is still throttled.
rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""

expect_nudge_sid 'session A: symbol grep -> nudge' \
  'grep -rn "class Session\|class Alpha" other_src' "session-A"
expect_nudge_sid 'session B (different session_id): symbol grep right after session A -> still nudges (not throttled by session A)' \
  'grep -rn "class Session\|class Beta" other_src' "session-B"
expect_no_nudge_sid 'session A again immediately after -> throttled within its own session' \
  'grep -rn "class Session\|class Gamma" other_src' "session-A"

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

# ── Read-tool cap: whole-file .cs reads (no offset/limit) over a 2000-char budget are
# character-capped (not line-capped) to however many leading lines fit that budget, clamped
# to a 500-line ceiling; non-C# files pass through; silent for files under the budget,
# missing files, or when offset/limit is given; deterministic (not throttled). ──────────────
BIG_FILE="$WORK/big_file.cs"
seq 1 500 > "$BIG_FILE"
SMALL_FILE="$WORK/small_file.cs"
seq 1 10 > "$SMALL_FILE"
MISSING_FILE="$WORK/does_not_exist.cs"
# Char-dense but SHORT file: 50 lines of 800 chars each (40050 bytes) -- well over the 2000-
# char budget despite having far fewer than 500 lines. This is exactly the case a line-only
# cap misses.
DENSE_FILE="$WORK/dense_file.cs"
line800="$(printf 'x%.0s' $(seq 1 800))"
yes "$line800" | head -50 > "$DENSE_FILE"
DENSE_MARKDOWN_FILE="$WORK/dense_file.md"
cp "$DENSE_FILE" "$DENSE_MARKDOWN_FILE"
DENSE_TEXT_FILE="$WORK/dense_file.txt"
cp "$DENSE_FILE" "$DENSE_TEXT_FILE"
# Large file with short, uniform lines (1500 lines x 2 bytes) so the computed limit exceeds
# the 500-line ceiling and gets clamped.
LARGE_FILE="$WORK/large_file.cs"
yes a | head -1500 > "$LARGE_FILE"

# compute_expected_cap_limit <file> — mirrors the hook's own awk formula (see
# hooks/tk-route.sh, _emit_read_cap call site) plus the READ_CAP_LINES clamp, so expected
# limits stay correct without hand-computing byte arithmetic per fixture.
compute_expected_cap_limit() {
  local file="$1"
  local lim
  lim=$(awk -v cap="2000" '{b+=length($0)+1; c++; if(b>=cap){print c; exit}} END{if(b<cap)print c+0}' "$file")
  (( lim > 500 )) && lim=500
  printf '%s' "$lim"
}
DENSE_EXPECTED_LIMIT="$(compute_expected_cap_limit "$DENSE_FILE")"
LARGE_EXPECTED_LIMIT="$(compute_expected_cap_limit "$LARGE_FILE")"

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

# expect_no_read_cap <desc> <file> <offset> <limit> [cwd]
# No cap and no other stdout at all: file under the byte budget, missing file, or offset/
# limit already given.
expect_no_read_cap() {
  local desc="$1" file="$2" offset="$3" limit="$4" cwd="${5:-/repo}"
  local before_log="$LOG_CONTENT"
  hook_run_read_keep_home "$FAKE_BIN" "$file" "$offset" "$limit" "$cwd"

  assert_eq "$desc: exit 0" "0" "$RC"
  assert_eq "$desc: no stdout" "" "$OUT"
  assert_eq "$desc: no new decision-log entry" "$before_log" "$LOG_CONTENT"
}

# expect_read_cap <desc> <file> <offset> <limit> <expected_limit> [cwd]
expect_read_cap() {
  local desc="$1" file="$2" offset="$3" limit="$4" expected_limit="$5" cwd="${6:-/repo}"
  hook_run_read_keep_home "$FAKE_BIN" "$file" "$offset" "$limit" "$cwd"

  assert_eq "$desc: exit 0" "0" "$RC"
  local got_fp
  got_fp="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.updatedInput.file_path // empty')"
  assert_eq "$desc: updatedInput.file_path" "$file" "$got_fp"
  local got_limit
  got_limit="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.updatedInput.limit // empty')"
  assert_eq "$desc: updatedInput.limit" "$expected_limit" "$got_limit"
  local got_keys
  got_keys="$(printf '%s' "$OUT" | jq -c '.hookSpecificOutput.updatedInput | keys | sort')"
  assert_eq "$desc: updatedInput contains file_path and limit" '["file_path","limit"]' "$got_keys"
  local got_ctx
  got_ctx="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.additionalContext // empty')"
  assert_true "$desc: additionalContext present" "$([[ -n "$got_ctx" ]] && echo 1 || echo 0)"
  assert_true "$desc: decision-log line present (cap-read)" \
    "$([[ "$LOG_CONTENT" == *$'\t'"cap-read"$'\t'* ]] && echo 1 || echo 0)"
}

# Group 1: char-dense but short file (well over 2000 chars, far under 500 lines) caps -- the
# case a line-only cap misses entirely. Deterministic: fires again on an immediate repeat.
rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""
expect_read_cap "char-dense short file, no offset/limit -> capped" "$DENSE_FILE" "" "" "$DENSE_EXPECTED_LIMIT"
expect_read_cap "cap fires again immediately after -> still capped (no throttle)" "$DENSE_FILE" "" "" "$DENSE_EXPECTED_LIMIT"

# Group 2: a large file with short lines still caps, and the computed limit is clamped to
# the 500-line ceiling.
rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""
expect_read_cap "large file, no offset/limit -> capped, limit clamped to 500" "$LARGE_FILE" "" "" "$LARGE_EXPECTED_LIMIT"
assert_eq "large file expected limit is clamped to the 500-line ceiling" "500" "$LARGE_EXPECTED_LIMIT"

# Group 3: silent cases -- no stdout at all, including non-C# files over the budget.
rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""
expect_no_read_cap "dense markdown file -> no cap" "$DENSE_MARKDOWN_FILE" "" ""
expect_no_read_cap "dense text file -> no cap" "$DENSE_TEXT_FILE" "" ""
expect_no_read_cap "small file (<2000 chars), no offset/limit -> no cap" "$SMALL_FILE" "" ""
expect_no_read_cap "big file (500 lines, <2000 chars), no offset/limit -> no cap" "$BIG_FILE" "" ""
expect_no_read_cap "missing file -> no cap" "$MISSING_FILE" "" ""
expect_no_read_cap "dense file with offset given -> untouched (no cap)" "$DENSE_FILE" "100" ""
expect_no_read_cap "dense file with limit given -> untouched (no cap)" "$DENSE_FILE" "" "10"
expect_no_read_cap "dense file with offset and limit given -> untouched (no cap)" "$DENSE_FILE" "10" "10"

# expect_stealth_rewrite_nudge <desc> <command> <expected_new_command>
# .cs stealth read that the fake rtk DOES recognize (cat/head/tail, mirroring real rtk):
# both the `rtk read ...` rewrite AND the tk-view nudge must be present together.
expect_stealth_rewrite_nudge() {
  local desc="$1" cmd="$2" expected_new="$3"
  hook_run_keep_home "$FAKE_BIN" "$cmd"

  assert_eq "$desc: exit 0" "0" "$RC"
  local got_new
  got_new="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.updatedInput.command // empty')"
  assert_eq "$desc: updatedInput.command" "$expected_new" "$got_new"
  local got_ctx
  got_ctx="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.additionalContext // empty')"
  assert_true "$desc: additionalContext (nudge) present" "$([[ -n "$got_ctx" ]] && echo 1 || echo 0)"
  assert_true "$desc: decision-log line present (nudge)" \
    "$([[ "$LOG_CONTENT" == *$'\t'"nudge"$'\t'* ]] && echo 1 || echo 0)"
}

# expect_stealth_rewrite_no_nudge <desc> <command> <expected_new_command>
# Throttled repeat: the rewrite still fires every time (deterministic, not throttled), but
# the nudge itself is suppressed (throttle only ever suppresses additionalContext, never
# the rewrite).
expect_stealth_rewrite_no_nudge() {
  local desc="$1" cmd="$2" expected_new="$3"
  hook_run_keep_home "$FAKE_BIN" "$cmd"

  assert_eq "$desc: exit 0" "0" "$RC"
  local got_new
  got_new="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.updatedInput.command // empty')"
  assert_eq "$desc: updatedInput.command (rewrite still happens)" "$expected_new" "$got_new"
  local got_ctx
  got_ctx="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.additionalContext // empty')"
  assert_eq "$desc: no additionalContext (nudge throttled)" "" "$got_ctx"
}

# ── Bash: stealth .cs reads via cat/sed/head/tail nudge toward tk view / tk def|refs|callers.
# Real rtk (and the fake rtk stub above) rewrites cat/head/tail of a .cs file to
# `rtk read <file>` -- the nudge must be attached to that rewrite, not swallowed by it. sed
# is NOT recognized by real rtk, so it nudges standalone with no rewrite. Scoped to .cs only;
# shares the "nudge" throttle bucket with the symbol-grep nudge. ───────────────────────────
rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""
expect_stealth_rewrite_nudge "cat a .cs file -> rewritten to rtk read AND nudged" \
  "cat foo.cs" "rtk read foo.cs"
expect_stealth_rewrite_no_nudge "second stealth .cs read immediately after -> still rewritten, nudge throttled" \
  "head foo.cs" "rtk read foo.cs"

rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""
expect_nudge "sed -n on a .cs file -> nudge only (real rtk doesn't rewrite sed)" \
  "sed -n '1,40p' foo.cs"

rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""
expect_stealth_rewrite_nudge "tail a .cs file -> rewritten to rtk read AND nudged" \
  "tail foo.cs" "rtk read foo.cs"

rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""
expect_no_nudge "cat a .txt file -> no nudge, and rtk doesn't recognize it either (scoped to .cs only)" \
  "cat foo.txt"
expect_no_nudge "unrelated passthrough command -> no stealth-read nudge" "kubectl get pods"

# ── Edit tool: large native replacements nudge toward `tk write`. Never blocks/rewrites the
# Edit itself (no updatedInput ever produced for this tool). Throttled like the other nudges,
# keyed by session_id via the shared "nudge-edit-write" decision bucket. ───────────────────

# hook_run_edit_keep_home <path> <file_path> <old_string> [cwd]
# Does not reset FAKE_HOME first, so the throttle (previous "nudge-edit-write" decision-log
# timestamp) can be exercised across invocations, mirroring hook_run_keep_home.
hook_run_edit_keep_home() {
  local path="$1" file_path="$2" old_string="$3" cwd="${4:-/repo}"
  mkdir -p "$FAKE_HOME/.claude"

  local input
  input=$(jq -n --arg f "$file_path" --arg o "$old_string" --arg cwd "$cwd" '
    {tool_name:"Edit", cwd:$cwd,
     tool_input:{file_path:$f, old_string:$o, new_string:"replacement"}}')

  OUT="$(PATH="$path" HOME="$FAKE_HOME" bash "$HOOK" <<<"$input" 2>"$WORK/stderr")"
  RC=$?
  ERR="$(cat "$WORK/stderr")"
  if [[ -f "$(today_log_file)" ]]; then
    LOG_CONTENT="$(cat "$(today_log_file)")"
  else
    LOG_CONTENT=""
  fi
}

# expect_edit_write_nudge <desc> <file_path> <old_string> [cwd]
expect_edit_write_nudge() {
  local desc="$1" file_path="$2" old_string="$3" cwd="${4:-/repo}"
  hook_run_edit_keep_home "$FAKE_BIN" "$file_path" "$old_string" "$cwd"

  assert_eq "$desc: exit 0" "0" "$RC"
  local got_ctx
  got_ctx="$(printf '%s' "$OUT" | jq -r '.hookSpecificOutput.additionalContext // empty')"
  assert_true "$desc: additionalContext (tk write nudge) present" "$([[ -n "$got_ctx" ]] && echo 1 || echo 0)"
  local got_updated
  got_updated="$(printf '%s' "$OUT" | jq -r 'if (.hookSpecificOutput | has("updatedInput")) then "present" else "" end')"
  assert_eq "$desc: no updatedInput (Edit unchanged, not blocked)" "" "$got_updated"
  assert_true "$desc: decision-log line present (nudge-edit-write)" \
    "$([[ "$LOG_CONTENT" == *$'\t'"nudge-edit-write"$'\t'* ]] && echo 1 || echo 0)"
}

# expect_no_edit_write_nudge <desc> <file_path> <old_string> [cwd]
expect_no_edit_write_nudge() {
  local desc="$1" file_path="$2" old_string="$3" cwd="${4:-/repo}"
  local before_log="$LOG_CONTENT"
  hook_run_edit_keep_home "$FAKE_BIN" "$file_path" "$old_string" "$cwd"

  assert_eq "$desc: exit 0" "0" "$RC"
  assert_eq "$desc: no stdout" "" "$OUT"
  assert_eq "$desc: no new decision-log entry" "$before_log" "$LOG_CONTENT"
}

LARGE_OLD_STRING="$(printf 'x%.0s' $(seq 1 500))"
SMALL_OLD_STRING="$(printf 'x%.0s' $(seq 1 50))"

rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""
expect_edit_write_nudge "Edit with old_string >= 500 chars -> tk write nudge" \
  "/repo/Foo.cs" "$LARGE_OLD_STRING"
expect_no_edit_write_nudge "second qualifying Edit immediately after -> throttled, not nudged" \
  "/repo/Foo.cs" "$LARGE_OLD_STRING"

rm -rf "$FAKE_HOME"; mkdir -p "$FAKE_HOME/.claude"; LOG_CONTENT=""
expect_no_edit_write_nudge "Edit with old_string < 500 chars -> no nudge" \
  "/repo/Foo.cs" "$SMALL_OLD_STRING"

echo "# --- $PASS passed, $FAIL failed ---"
[[ "$FAIL" -eq 0 ]]
