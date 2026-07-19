Задачи/идеи:

  1. Оставить Read cap >500 строк.
     Он режет дорогие промахи: мало событий, но заметная доля full-read
     chars.

  2. Не делать hard-ban repeated full-read.
     Повторы дают ограниченную экономию и часто легитимны: файл мог
     меняться, контекст мог потеряться.

  3. Добавить мягкий repeat hint.
     Только если тот же файл уже full-read в этой сессии и hash не
     изменился: “ты уже читал файл, лучше slice”.

  4. Переделать tk view для .cs в LSP-first semantic outline.
     Вместо legacy hot regex ranges показывать:
     kind name(signature) start-end body_size.

  5. Оставить regex fallback.
     Для non-C# и когда LSP недоступен: текущая эвристика, но помечать
     source=regex approx.

  6. Сделать Read hint для больших .cs файлов не про tk def <Name>, а
     про tk view <file>.
     Потому что агент часто ещё не знает symbol name.

  7. Новый flow:
     tk view BigFile.cs → увидеть методы/ranges → Read offset/limit
     только нужного метода.

  8. Метрика успеха:
     после capped/nudged .cs full-read в следующих tool calls больше tk
     view/tk def/sliced Read, меньше uncapped full-read.


# Agent tooling backlog

This backlog collects the broader ideas discussed around `tk`, Claude Code, Codex, `rtk`,
oh-my-pi-style harnesses, and agent performance. It is intentionally broader than the hook
routing analysis: some items are concrete `tk` work, some are research ideas, and some are
explicitly not worth doing yet.

## Current decisions (2026-07)

Agreed priorities and rules from the routing discussion; details in `hook-routing-analysis.md`.

Hook routing first, in this order:

1. **Downstream-guard bug fix**: segment rewrite must not route a pipe LHS when the RHS is a
   content filter (`grep`, `awk`, `sed`, `cut`, `wc`) — filtering compact output silently loses
   matches (`dotnet test | grep FAIL`). Rewrite only when all downstream consumers are display
   modifiers (`head`, `tail`, `cat`).
2. **Newline as segment separator**: treat bare `\n` as `;` in the compound rewriter (heredocs,
   control flow, line continuations stay passthrough). Opens the largest untouched bucket
   (`multi_line_script`, ~1.07M shown chars).
3. **Env prefixes**: strip leading `VAR=val` in `_rewrite_segment`, insert `tk` after them.
4. **Per-session nudge throttle**: current throttle reads a shared daily log, so a main-agent
   nudge suppresses nudges for freshly spawned subagents — the ones that need them most. Key the
   throttle by `session_id` from hook input.
5. Tests + README + re-run stats before deciding on `find`/`ls` routes or anything bigger.

Grep policy — rewrite vs deny rule: **rewrite when tk answers the same question compactly
(`dotnet build` → `tk dotnet build`); deny/nudge when the mapping guesses intent
(`grep "class Foo"` → `tk def`)**. Auto-rewriting grep to LSP silently answers a different
question and fails wrong (empty tk def result reads as "symbol doesn't exist"). Deny gives an
explicit refusal with a reason, keeps an escape hatch (agent can rephrase a genuine text grep),
and teaches the agent within the session. First step is measurement, not enforcement: add a grep
pattern classifier to `Stats/bash_opportunities.py` (symbol-shaped vs free-text) — hard-deny only
ultra-high-confidence forms (`class|interface|record \w+`) if the data shows they dominate.

`tk view` universal (parallel track, independent of hooks): dispatch on argument type — repo/dir
card, `.csproj` project card, `.sln` solution card, log delegate, source symbol map. No new
grammar; view stays orientation, exact reads stay native Read. Dir card is also the routing
target `find`/`ls` compounds need, so it precedes those routes.

## Product direction

`tk` should not try to become a full agent harness. Claude Code, Codex, and similar providers
own the model-specific tool loop, retry behavior, internal prompting, and product integration.
`tk` should stay in the utility/tooling layer:

- keep native command syntax where agents already know it
- make noisy commands compact and complete
- add semantic `.NET`/repo helpers where native shell commands are weak
- avoid growing the public agent surface into a large command zoo
- prefer hooks/proxies that let agents keep writing familiar commands

The useful split:

| Command type | Preferred interface |
|---|---|
| Known native commands | Agent writes normal command; hook/proxy routes/compresses when safe |
| Exact file reads/search | Prefer native Read/Grep unless a `tk` semantic view is explicitly needed |
| Semantic code navigation | Explicit `tk def` / `tk refs` / `tk callers` / `tk rename` |
| Noisy validation | Native `dotnet build/test/restore`, routed to `tk` |
| New repo intelligence | Small explicit `tk` commands, not many narrow aliases |

## Already in tk or recently added

- Compact `dotnet build`, `dotnet test`, and `dotnet restore`.
- Compact git status/log/diff/show commands, with extra caution around diffs.
- `tk changes` as a compact working-tree summary.
- Repo orientation tools: `tree`, `files`, `focus`, `view`.
- ASP.NET/log filtering via `tk log`.
- LSP/Roslyn-backed `def`, `refs`, `callers`, `impl`, `diag`, `rename`.
- `tk mv` for file moves with namespace/reference assistance.
- Module/init snippets for Claude/Codex.
- Claude hook router in `hooks/tk-route.sh`.
- Hook nudges for symbol-like grep and large whole-file reads.
- Stats analyzer for Bash opportunities and compound command breakdowns.
- Conservative segment-aware hook rewrite for compound commands:
  - routes `dotnet build|test|restore`
  - routes `git status|log`
  - preserves shell glue and pipeline modifiers
  - keeps `git diff/show` raw

## High-value backlog

### 1. Keep improving hook routing before adding more commands

The stats showed that the largest performance issue is not missing commands. It is agents,
especially subagents, issuing compound Bash commands that bypass routing:

```bash
cd x && dotnet build | head -50
find . -type f | head -60
grep -r "Foo" src | head -50
git status && echo "---DIFF---" && git diff | head -100
```

Current next work:

- update `hooks/README.md` for the new compound rewrite behavior
- add more tests for `||`, `git diff && git status`, substitutions, env prefixes, quoted operators
- consider safe `find` / `ls` routing inside compounds
- keep `head`, `tail`, `sort`, `wc`, `cd`, `echo` as native shell glue
- keep `git diff/show` raw by default
- measure again after real usage

Do not jump straight to `tk shell` unless segment rewriting leaves a large unresolved bypass.

### 2. Smart edit / line+hash patching

Original idea: avoid brittle `old_string` replacement by anchoring edits to line ranges plus file
hashes or hash lines.

Possible interface:

```text
tk patch apply
[src/Foo.cs#8fa31c]
replace 57-59:
  var reserve = CalculateReserve(customer);
```

Value:

- lowers output tokens for mechanical edits
- avoids huge old/new string blocks
- detects stale files before writing
- better for bulk mechanical rewrites

Important product boundary:

- do not replace provider-native Edit everywhere
- keep this as an optional utility for mechanical/bulk/high-confidence edits
- require stale-state detection and clear failure modes

Priority: medium. Useful, but hook routing and discovery/output issues are currently more data-backed.

### 3. Smarter `tk view` and repo/project cards

Current `tk view` is useful as orientation, but broader smart-read ideas are still backlog.

Potential work:

- `tk view .` as repo overview
- `tk view <dir>` as directory/repo card
- `tk view <project.csproj>` as project card
- `tk view <solution.sln>` as solution graph/card
- `tk view <log-file>` delegating to log filtering
- `.csproj` card:
  - target frameworks
  - package refs
  - project refs
  - output type
  - nullable/implicit usings
- solution card:
  - apps/libs/tests
  - project references
  - target frameworks
  - obvious cycles/skew

Constraint:

- do not teach agents a large new grammar
- if a native command is already clear, preserve native syntax
- `tk view` remains orientation; exact reads should still use native Read/slices

Priority: medium-high for `.NET` repos. Project/solution cards are probably more valuable than
more leaf commands.

### 4. LSP-first workflow

This is one of `tk`'s strongest differentiators.

Continue to push agents toward:

- `tk def <symbol>`
- `tk refs <symbol>`
- `tk callers <symbol>`
- `tk impl <symbol>`
- `tk diag <file|project|dir>`
- `tk rename <file:line:col> <newName>`

Potential improvements:

- expose Roslyn code actions:
  - add missing using
  - implement interface
  - generate constructor
  - remove unused usings
  - simplify expression
- improve `diag` guidance as faster first check than `dotnet build` after small edits
- add better ambiguity cards for symbol lookup
- keep symbol grep nudges, but make them less noisy and more targeted

Priority: high for `.NET`; this is semantic value that native shell does not provide.

### 5. Targeted validation / affected tests

Agents waste time and tokens on full builds/tests.

Possible commands or behavior:

- `tk affected`
- `tk test suggest`
- `tk test affected`
- infer changed projects from `.sln`/`.csproj` graph
- suggest specific `dotnet test --filter ...`
- compact output showing why a target was selected

This should be `.NET`-aware and conservative. It should not hide full validation when release risk
requires it.

Priority: medium-high for large .NET repos.

### 6. Project conventions card

Command idea:

```bash
tk conventions
```

Collect compact repo-local facts from:

- `AGENTS.md`
- `.editorconfig`
- `Directory.Build.props`
- `Directory.Packages.props`
- `global.json`
- test frameworks
- nullable/implicit usings
- analyzers
- local naming patterns

Output should be short and stable:

```text
nullable=enable
packages=centralized
tests=xunit FluentAssertions
style=file-scoped namespaces
architecture=Domain/Application/Infrastructure
```

Priority: medium. Good input-token saver, but needs careful curation to avoid hallucinated rules.

## Medium-value backlog

### 7. AST / structural rewrite

Inspired by `ast-grep` / oh-my-pi `ast_edit`.

Use cases:

- replace `console.log($X)` with `logger.debug($X)`
- mechanical C# API migrations
- repeated pattern cleanup across many files

Requirements:

- preview first
- accept/discard
- atomic all-or-nothing write
- never silently modify huge file sets

Priority: medium. Useful for broad refactors, but less urgent than routing and LSP.

### 8. Preview/accept mode for dangerous operations

Useful for:

- AST rewrites
- mass rename/move
- formatting changed files
- dependency upgrades
- generated patch application

Shape:

```bash
tk change preview ...
tk change accept <id>
tk change discard <id>
```

Or keep it command-specific if a generic `change` grammar feels too harness-like.

Priority: medium.

### 9. Format changed files only

Possible command:

```bash
tk format changed
tk format changed --verify
```

Under the hood:

- find changed `.cs` files
- run `dotnet format --include <files>`
- keep output compact
- avoid formatting the entire solution by accident

Priority: medium. Small blast-radius improvement.

### 10. NuGet/package intelligence

Possible commands:

- `tk nuget map`
- `tk nuget outdated`
- `tk nuget vuln`
- `tk nuget why <Package>`

Useful outputs:

- version skew
- centralized package status
- packages per project
- vulnerability summary without restore spam

Priority: medium-low unless package work becomes frequent.

### 11. Log request/correlation views

Extend `tk log` toward backend incident work:

- `tk log app.log --trace <correlationId>`
- `tk log app.log --request POST /payroll`
- `tk log app.log --since 10m`
- `tk log app.log --exceptions`

Priority: medium for backend debugging, especially ASP.NET/MassTransit logs.

### 12. Token metrics and regression tracking

The product is token reduction, so token metrics should be measurable.

Possible work:

- per-command `tok=`, `rawTok=`, `saved=`
- regression tests that fail when a filter gets much chattier
- stats report comparing raw vs compact output
- support common tokenizers if practical

Priority: medium. Good for proving value, but do not overfit exact tokenizer costs.

## Lower-priority or research backlog

### 13. `tk shell --b64` + PATH shims

Alternative to segment rewrite:

```bash
tk shell --b64 <original-command>
```

Run the original shell command with a temporary PATH containing shims:

- `dotnet`
- `git`
- `rg`
- `grep`
- `find`
- `ls`

Value:

- catches commands inside arbitrary shell structure without parsing everything
- preserves `cd`, pipes, redirects, and shell behavior naturally

Risks:

- recursion through PATH
- quoting/base64 portability
- stdout/stderr ordering
- shell mismatch
- pipeline exit behavior
- security of temp shim directory
- Windows/pwsh parity

Priority: research only. Segment rewrite is cheaper and safer as the first step.

### 14. Debugger / DAP integration

Inspired by oh-my-pi's debugger support.

Value:

- avoids repeated printf-debug loops
- inspect stack/variables/breakpoints directly
- especially useful for complex runtime bugs

Possible shape:

- `tk debug start`
- `tk debug breakpoint`
- `tk debug stack`
- `tk debug vars`
- `tk debug eval`

Priority: low for now. Big surface area; only worth it if debugging transcripts show repeated
edit-run-print loops.

### 15. Persistent eval worker

Inspired by persistent Python/Bun workers.

Value:

- avoid restarting analysis processes
- keep parsed repo/data state
- useful for stats, CSV/JSON analysis, dependency graphs

Risk:

- starts drifting toward a harness
- state invalidation and lifecycle become non-trivial

Priority: low unless stats/data-analysis loops dominate.

### 16. In-process search/glob/shell

oh-my-pi avoids shelling out for search/glob/find and keeps persistent shell primitives.

For `tk`, this is probably not the right immediate direction. The lighter version is:

- keep native commands native
- hook/proxy safe cases
- cache where simple
- avoid building a full shell/search runtime

Priority: low.

### 17. Rule triggers / streaming corrections

oh-my-pi idea: do not put every rule into prompt; trigger specific reminders when the model starts
emitting risky patterns.

Examples:

- empty `catch { }`
- broad swallowed errors
- dangerous APIs
- forbidden project-local patterns

In `tk`, the nearest equivalent is hook nudges. Could extend nudges, but avoid noisy correction
loops.

Priority: low-medium, only for high-signal patterns.

### 18. Project memory / curated summaries

Potential value:

- remember repo commands
- test strategy
- architecture boundaries
- package/feed quirks
- generated-file rules

But this is partly provider/harness territory. For `tk`, a `tk conventions` command is probably the
safer first step.

Priority: low-medium.

### 19. Checkpoint / rewind / context pruning

Useful in full harnesses: prune failed exploration and keep only the final causal summary.

This does not fit `tk` as a CLI utility except indirectly through stats/docs. Leave to providers.

Priority: not a `tk` feature.

### 20. Cheap model roles / advisor model / typed subagents

Useful in orchestration:

- cheap model for summarization/commit messages
- strong advisor before risky edits
- typed JSON subagent results instead of prose

This is outside `tk` unless `tk` is generating subagent prompts or analyzing transcripts.

Priority: not a `tk` feature.

## Explicit non-goals for now

- Do not replace Claude Code/Codex harness behavior.
- Do not teach agents dozens of narrow new commands.
- Do not auto-route commands just because a `tk` command with a similar name exists.
- Do not auto-compact `git diff`/`git show` where patch fidelity matters.
- Do not route exact file reads (`cat`, `sed -n`) to `tk view`.
- Do not blindly rewrite all `grep`/`rg` to `tk focus`.
- Do not add `tk shell`/PATH shims before measuring whether segment rewrite is insufficient.

## Suggested next decisions

1. Decide supported `find` / `ls` route shapes.
   Good candidates are simple orientation forms followed by `| head` or `| wc`.

2. Decide the `grep` policy.
   Separate:
   - exact grep that should remain raw
   - broad grep that can become compact search
   - symbol grep that should be nudged to LSP

3. Update hook docs/tests.
   The code changed; the hook README still describes the older single-command-only policy.

4. Re-run stats after enough real sessions.
   Look for whether compound bypasses drop and what remains.

5. Only then revisit `tk shell --b64`.
   Use it if the remaining bypasses are mostly shell structures that segment rewrite cannot safely
   handle.
