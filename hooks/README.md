# tk-route hook

A Claude Code `PreToolUse` hook that routes single-command Bash calls through `tk`
(or, as a fallback, `rtk`) for compact output, and nudges grep-for-symbol toward
semantic navigation.

## What it does

- **Rewrites to `tk`**: single `dotnet build|test|restore` and `git status|log`
  commands become their `tk` equivalents before execution, so Claude gets compact,
  token-efficient output instead of raw verbose output. A leading run of `-C <path>` /
  `-c <key=val>` global git flags before the subcommand is allowed and preserved
  (e.g. `git -C ../svc-a status`, `git -c core.pager=cat -C /x/y log --oneline`).
  `git diff`/`git show` (with or without the same `-C`/`-c` prefix) are intentionally
  NOT routed (to either `tk` or `rtk`) — the agent often needs the full patch, and
  compaction can drop added/changed lines and mislead it.
- **Falls back to `rtk`**: any other safe, single command not covered above is offered
  to `rtk rewrite <cmd>` (if `rtk` is installed on PATH). If `rtk` recognizes it, the
  rewritten command replaces the original; if `rtk` doesn't recognize it (exit 1) or
  isn't installed, the command passes through unchanged.
- **Nudges** grep/egrep/fgrep/rg commands that look like a symbol search — either the
  `class|record|interface|enum|struct <Identifier>` keyword shape, or a bare
  alternation of two capitalized identifiers (`"FooService\|BarService"` /
  `"FooService|BarService"`) — with a hint to prefer semantic symbol navigation. This
  does not modify the command (`additionalContext` only), so it fires even for
  compound/unsafe commands that are never rewritten.
- **Nudges large whole-file `Read`s**: a `Read` with no `offset`/`limit` on a regular
  file over 400 lines gets an `additionalContext` hint to locate the relevant slice
  first (codebase-memory `get_code_snippet`, `tk def <Symbol>`, or `tk view <file>`)
  and then re-read just that range. The command/input is never modified.

## Safety

- **Quote/escape-aware unsafe detection**: the rewrite paths (`tk`/`rtk`) only apply to
  safe, single commands. "Safe" is judged against a copy of the command with
  backslash-escapes and quoted (`'...'`/`"..."`) segments stripped out via `sed` —
  metacharacters inside quotes or escaped with `\` are literal text to the shell, not
  operators, so e.g. `grep "class A\|class B"` or `git log --grep="fix && cleanup"` are
  correctly treated as single safe commands instead of false-flagged as compound. A
  quote left unbalanced after stripping means the rest can't be reasoned about, so it's
  treated as unsafe. Real, unquoted/unescaped `|`, `&&`, `;`, `` ` ``, `$(`, a trailing
  background `&`, or an embedded newline still mark a command unsafe and leave it
  completely untouched (the nudge check is the one exception — see above).
- **Never double-wraps**: if the command already starts with `tk ` or `rtk `, it is not
  touched.
- **Nudge throttle**: at most one nudge per 300 seconds, judged by the timestamp of the
  last `nudge` line in the decision log — so a grep-heavy loop doesn't get re-nudged on
  every call. If the log is missing or unreadable, the throttle is skipped (just nudge).
- **Degrades to a no-op** (design philosophy): every failure mode here — `jq` missing,
  `rtk` missing, `rtk` not recognizing a command, the log being unwritable — is handled
  by silently doing nothing and letting the original command run unchanged. The hook
  never blocks or errors a command; if the running Claude Code version ignores
  `updatedInput`/`additionalContext` entirely, the hook is a pure no-op. Nothing about
  this hook's absence, malfunction, or a missing dependency should ever break a command.

## Known gaps

- **`jq` absent → silent no-op**: if `jq` isn't on PATH, the hook can't parse its own
  input and exits 0 with no output and no stderr noise; the original command runs as-is.

## How to activate

`tk` is a global tool (its guidance lives in `~/.claude/CLAUDE.md`), so the hook
that enforces that guidance belongs in your **global** settings too. Install it
once, in one place — do not enable both, or it fires twice per command. The
matcher must be `Bash|Read` — a matcher of just `Bash` will silently skip the
large-file-read nudge below.

### Recommended — Global (all repos)

Add to `~/.claude/settings.json`, using the absolute path to the script:

```json
"hooks": {
  "PreToolUse": [
    {
      "matcher": "Bash|Read",
      "hooks": [
        {
          "type": "command",
          "command": "/Users/artsiom/Developer/other/tk/hooks/tk-route.sh",
          "timeout": 5
        }
      ]
    }
  ]
}
```

### Alternative — Project-scoped (this repo only)

If you'd rather scope it to one repo, add the same block to
`.claude/settings.local.json` (gitignored) with `${CLAUDE_PROJECT_DIR}/hooks/tk-route.sh`
as the command instead of the absolute path. Don't combine this with the global
install.

## Decision log

Every time the hook takes an action it appends one tab-separated line to a global,
per-day file, `~/.claude/tk-hook-YYYYMMDD.log` (one file per day, not per-repo, so a
globally installed hook never litters working trees and the nudge throttle below only
ever greps a single day's lines instead of an ever-growing file). Each line carries the
`cwd` column to attribute the action to a repo:

```
<epoch_seconds>\t<decision>\t<cwd>\t<command>
```

`<decision>` is one of:

- `route-tk` — command was rewritten to its `tk` equivalent (e.g. `dotnet build` →
  `tk dotnet build`)
- `route-rtk` — command was rewritten by the `rtk` fallback (e.g. `docker ps` →
  `rtk docker ps`)
- `nudge` — symbol-grep was detected and the hint was emitted (additionalContext only;
  the command itself is not changed, and this can fire on unsafe/compound commands too)
- `nudge-read` — a large whole-file `Read` (no `offset`/`limit`, file over 400 lines)
  was detected and the hint was emitted (additionalContext only; the `command` column
  holds the file path instead of a shell command). Throttled independently of `nudge`.

Skip-throughs (no match, compound/unsafe commands, `rtk` declining a command, etc.) are
not logged; only the active cases above are recorded. The log is append-only and safe to
delete at any time — the hook will recreate it. If `$HOME` is unavailable or the path is
not writable, logging is silently skipped; the hook still completes normally. Rotation is
by wall-clock date only — there is no migration of a pre-rotation `~/.claude/tk-hook.log`,
and nothing deletes it; any tooling that mines hook-log history across days needs to glob
`tk-hook-*.log`.

## The nudge messages

- **Symbol-grep nudge**: when triggered, the `additionalContext` is a two-headed
  message that recommends the codebase-memory MCP tools (`search_graph`/`trace_path`)
  first, and `tk def|refs|callers` as the fallback when MCP is unavailable or the repo
  isn't indexed — since either one resolves cross-file and crosses interface dispatch,
  unlike a text grep for `class X`.
- **Large-read nudge**: when a `Read` has no `offset`/`limit` and the target file is a
  regular file over 400 lines, the `additionalContext` suggests locating the relevant
  slice first (codebase-memory `get_code_snippet`, `tk def <Symbol>`, or `tk view
  <file>` for a structure map) and then re-reading just that range with
  `offset`/`limit`. A full read is still correct when editing or reviewing the whole
  file; this only nudges, it never blocks or rewrites the `Read` input.

## Requirements

- `jq` on PATH — its absence is a silent no-op (see Known gaps), not a hard failure
- `tk` on PATH, and optionally `rtk` on PATH for the fallback rewrite path
- Standard POSIX utilities (`sed`, `grep`, `cut`, `tail`, `wc`) for the unsafe-detection
  stripping, the nudge throttles, and the large-file line count — present on any normal
  macOS/Linux install
