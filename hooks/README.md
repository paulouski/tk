# tk hooks

Two Claude Code `PreToolUse` hooks:

- **`tk-route.sh`** (matcher `Bash|Read`) routes Bash calls through `tk` (or, as a
  fallback, `rtk`) for compact output — both single commands and, conservatively,
  compound commands — and nudges grep-for-symbol toward semantic navigation.
- **`tk-agent-preamble.sh`** (matcher `Task`) appends a short tk cheat-sheet to every
  subagent prompt, so freshly spawned subagents get the tooling guidance without
  relying on the main agent to paste it by hand.

Plus one opencode plugin (TypeScript):

- **`tk-route-opencode.ts`** ports the same bash-routing and read-cap rules to
  opencode's `tool.execute.before` hook (see "opencode" section below).

## What tk-route does

- **Rewrites to `tk`**: single `dotnet build|test|restore` and `git status|log`
  commands become their `tk` equivalents before execution, so Claude gets compact,
  token-efficient output instead of raw verbose output. A leading run of `-C <path>` /
  `-c <key=val>` global git flags before the subcommand is allowed and preserved
  (e.g. `git -C ../svc-a status`, `git -c core.pager=cat -C /x/y log --oneline`), as
  is a leading run of `VAR=val` environment prefixes (`FOO=bar dotnet build` →
  `FOO=bar tk dotnet build`).
  `git diff`/`git show` (with or without the same `-C`/`-c` prefix) are intentionally
  NOT routed (to either `tk` or `rtk`) — the agent often needs the full patch, and
  compaction can drop added/changed lines and mislead it.
- **Rewrites inside compound commands**: commands joined by `&&`, `||`, `;`, `|`, or
  bare newlines used as statement separators get a quote-aware segment rewrite for
  the conservative built-in set above (`tk` only — no `rtk` fallback inside
  compounds). Each pipeline is rewritten as a unit under a **three-tier downstream
  guard**, so a routed left-hand side never silently starves a downstream content
  filter:
  - **Tier 1 — display modifiers only** (`head`, `tail`, `cat` downstream): the
    left-hand side is routed, the pipeline is kept.
    `cd x && dotnet build | head -50` → `cd x && tk dotnet build | head -50`
  - **Tier 2 — problem-shaped grep over dotnet**: when the left-hand side is
    `dotnet build|test|restore` and every downstream consumer is a
    `grep`/`egrep`/`rg` whose pattern is clearly hunting problems
    (`fail|error|warn|CS####|expected|received|assert|passed`, case-insensitive,
    with none of `-v -c -o -l -L`), the **entire pipeline is replaced** by the bare
    `tk` command — compact output already keeps every error/warning/failure, so the
    filter is dropped and an `additionalContext` hint tells the agent why and how to
    get the raw stream back (`tk --raw dotnet ...`).
    `dotnet test 2>&1 | grep -E "Passed|Failed" | head -20` → `tk dotnet test 2>&1`
  - **Tier 3 — anything else downstream** (`grep` with any other pattern or with
    `-v`/`-c`/`-o`, `awk`, `sed`, `cut`, `wc`, `sort`, ...): the whole pipeline is
    left untouched. The agent's own filter already limits the output, and rewriting
    the producer would silently change what the filter sees.
  Shell glue (`cd`, `echo`, ...) is preserved as-is. Heredocs, backslash-newline
  continuations, command substitutions, and shell control-flow keywords
  (`if`/`for`/`while`/...) mark the whole command as a real script and leave it
  completely untouched.
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
  file over 400 lines (but at or under the 500-line cap below) gets an
  `additionalContext` hint to locate the relevant slice first (codebase-memory
  `get_code_snippet`, `tk def <Symbol>`, or `tk view <file>`) and then re-read just
  that range. The command/input is never modified. Throttled (see below).
- **Caps large whole-file `Read`s over 500 lines**: a `Read` with no `offset`/`limit`
  on a regular file over 500 lines gets `updatedInput.limit` set to 500, so the call
  actually returns only the first 500 lines instead of the whole file, plus an
  `additionalContext` hint to continue with explicit `offset`/`limit` or to locate the
  relevant slice first. Unlike the nudge above, this is deterministic — it fires on
  every qualifying call, not throttled — and it's the one case where `tk-route.sh`
  rewrites a `Read`'s input rather than only attaching a hint. 500 lines was chosen
  from `Stats/bash_opportunities.py --read-cost` data on real transcripts: of uncapped
  `Read`s over 400 lines, a 500-line cutoff captures the bulk (~71%) of their chars.
  Any `Read` with an explicit `offset` or `limit` is left completely untouched by
  both the nudge and the cap.

## Safety

- **Quote/escape-aware detection**: operator detection is judged against a copy of the
  command with backslash-escapes and quoted (`'...'`/`"..."`) segments stripped out via
  `sed` — metacharacters inside quotes or escaped with `\` are literal text to the
  shell, not operators, so e.g. `grep "class A\|class B"` or
  `git log --grep="fix && cleanup"` are correctly treated as single safe commands
  instead of false-flagged as compound. A quote left unbalanced after stripping means
  the rest can't be reasoned about, so it's treated as unsafe. Unquoted/unescaped
  `&&`/`||`/`;`/`|`/newlines make a command *compound* (segment rewrite applies);
  `` ` ``, `$(`, a trailing background `&`, a backslash-newline continuation, a
  heredoc, or a control-flow keyword make it *hard-unsafe* and leave it completely
  untouched (the nudge check is the one exception — see above).
- **Never double-wraps**: if the command (or a compound segment) already starts with
  `tk ` or `rtk `, it is not touched.
- **Nudge throttle, keyed per session**: at most one nudge per 300 seconds *per
  `session_id`*, judged by the timestamp of the last matching `nudge` line in the
  decision log — so a grep-heavy loop doesn't get re-nudged on every call, but a nudge
  in the main agent's session never suppresses the first nudge for a freshly spawned
  subagent (each subagent has its own `session_id`, and they are the agents that need
  the nudge most). If the log is missing or unreadable, the throttle is skipped (just
  nudge).
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

`tk` is a global tool (its guidance lives in `~/.claude/CLAUDE.md`), so the hooks
that enforce that guidance belong in your **global** settings too. Install each
once, in one place — do not enable a hook both globally and per-project, or it
fires twice per command. The tk-route matcher must be `Bash|Read` — a matcher of
just `Bash` will silently skip the large-file-read nudge and cap below. The
agent-preamble hook uses its own entry with matcher `Task`.

### Recommended — Global (all repos)

Add to `~/.claude/settings.json`, using absolute paths to the scripts:

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
    },
    {
      "matcher": "Task",
      "hooks": [
        {
          "type": "command",
          "command": "/Users/artsiom/Developer/other/tk/hooks/tk-agent-preamble.sh",
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
ever greps a single day's lines instead of an ever-growing file). Each line carries a
`session_id` column (keys the per-session nudge throttle) and a `cwd` column to
attribute the action to a repo:

```
<epoch_seconds>\t<decision>\t<session_id>\t<cwd>\t<command>
```

`<decision>` is one of:

- `route-tk` — command was rewritten to its `tk` equivalent (e.g. `dotnet build` →
  `tk dotnet build`), including compound/pipeline rewrites (embedded newlines are
  collapsed to spaces so the entry stays one line)
- `route-rtk` — command was rewritten by the `rtk` fallback (e.g. `docker ps` →
  `rtk docker ps`)
- `nudge` — symbol-grep was detected and the hint was emitted (additionalContext only;
  the command itself is not changed, and this can fire on unsafe/compound commands too)
- `nudge-read` — a large whole-file `Read` (no `offset`/`limit`, file over 400 lines,
  at or under the 500-line cap) was detected and the hint was emitted (additionalContext
  only; the `command` column holds the file path instead of a shell command). Throttled
  independently of `nudge`.
- `cap-read` — a whole-file `Read` (no `offset`/`limit`, file over 500 lines) was
  capped: `updatedInput.limit` was set to 500 and the hint was emitted (the `command`
  column holds the file path). Not throttled — logged on every qualifying call.

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
  regular file over 400 lines (and at or under the 500-line cap), the
  `additionalContext` suggests locating the relevant slice first (codebase-memory
  `get_code_snippet`, `tk def <Symbol>`, or `tk view <file>` for a structure map) and
  then re-reading just that range with `offset`/`limit`. A full read is still correct
  when editing or reviewing the whole file; this only nudges, it never blocks or
  rewrites the `Read` input.
- **Large-read cap**: when a `Read` has no `offset`/`limit` and the target file is a
  regular file over 500 lines, `updatedInput.limit` is set to 500 so the call actually
  returns only the first 500 lines, and the `additionalContext` explains the file was
  capped and how to continue (explicit `offset`/`limit`, or locate the slice first).
  Escape hatch: pass an explicit `offset` or `limit` on the `Read` call and it is left
  completely untouched by both the nudge and the cap.

## The agent preamble (tk-agent-preamble.sh)

For every `Task` tool call (spawning a subagent), the hook appends a six-line
`[tk cheat-sheet]` block to the subagent's prompt via `updatedInput`: symbol lookup
with `tk def|refs|callers|impl` (never grep for symbols), `tk diag` after `.cs`
edits, run noisy commands bare (no defensive piping — the router compacts them),
escalate with `tk --more`/`--raw`, `tk mv` for file moves, and reading big files in
offset/limit slices (the tk-route.sh 500-line Read cap above). Idempotent: a prompt
already containing the `[tk cheat-sheet]` marker is left untouched, so nothing
double-appends. Non-`Task` tools, missing/empty prompts, and unparsable input all
exit as silent no-ops — same degrade-to-no-op philosophy as the router.

## Requirements

- `jq` on PATH — its absence is a silent no-op (see Known gaps), not a hard failure
- `tk` on PATH, and optionally `rtk` on PATH for the fallback rewrite path
- Standard POSIX utilities (`sed`, `grep`, `cut`, `tail`, `wc`) for the unsafe-detection
  stripping, the nudge throttles, and the large-file line count — present on any normal
  macOS/Linux install

## opencode

opencode (https://opencode.ai) has its own plugin system using `tool.execute.before`
hooks. Three plugin files, each installed as a separate file:

- **`hooks/tk-route-opencode.ts`** — bash routing + read cap (ports `tk-route.sh`'s core
  rules).
- **`hooks/tk-preamble-opencode.ts`** — subagent cheat-sheet preamble (ports
  `tk-agent-preamble.sh`).
- **`hooks/tk-slim-opencode.ts`** — trims tool-description and `read`-output token
  overhead that's unrelated to routing (opencode-only, no Claude Code equivalent).

All three degrade to a no-op on any error and can coexist as independent files.

### tk-route-opencode.ts

- **bash rewrite**: single and compound (`&&`/`||`/`;`/newline) `dotnet build|test|restore`
  and `git status|log` → `tk ...`. `git diff|show` stays raw. Heredocs, `$()`, backticks,
  and shell control keywords (`if`/`for`/`while`/...) are left untouched. A single
  (non-compound) command not recognized by the rules above is offered to `rtk rewrite`
  as a fallback (if `rtk` is on PATH) — mirrors `tk-route.sh`'s `rtk` fallback, but only
  for non-compound commands; compound/piped commands are handled exclusively by the
  quote-aware tokenizer and never reach the fallback.
- **read cap**: uncapped Reads of files >500 lines get `limit: 500`.

Not ported (no equivalent in opencode's `tool.execute.before`):
- the `additionalContext` nudges (symbol-grep, large-read) — opencode can mutate args but
  has no side-channel context channel like Claude Code's `additionalContext`.
- the three-tier pipeline guard's Tier 2 (problem-shaped grep replacement) — simplified to
  "rewrite LHS only when all downstream segments are display modifiers (head/tail/cat)".

### tk-preamble-opencode.ts

Ports `tk-agent-preamble.sh`: on the `task` tool, appends the `[tk cheat-sheet]` block to
`args.prompt` (idempotent on the marker).

### tk-slim-opencode.ts

opencode-only (no `tk-route.sh` equivalent): shortens the built-in tool descriptions sent
to the model, and compacts the `read` tool's output (relative `<path>`, drops the
`<type>file</type>` tag and the "(End of file - total N lines)" footer).

### How to activate (opencode)

Copy the plugins into your global opencode plugins directory:

```bash
mkdir -p ~/.config/opencode/plugins
cp hooks/tk-route-opencode.ts ~/.config/opencode/plugins/tk-route.ts
cp hooks/tk-preamble-opencode.ts ~/.config/opencode/plugins/tk-preamble.ts
cp hooks/tk-slim-opencode.ts ~/.config/opencode/plugins/tk-slim.ts
```

opencode auto-loads `.ts`/`.js` files from `~/.config/opencode/plugins/` at startup. Each
plugin needs `@opencode-ai/plugin` for the `Plugin` type; opencode resolves it from its
own runtime — no separate install step. After copying, restart opencode.

To verify `tk-route.ts` loaded, check that `dotnet build` output is compact after a bash
call. All plugins degrade to a no-op on any error (file unreadable, no match, etc.) — none
of them ever blocks or breaks a command.
