# Hook routing analysis

This note records the routing/performance investigation around `tk` hooks, compound Bash
commands, and subagent behavior. It is intentionally a status/design document: what was tried,
what the data says, what changed, and what should be prioritized next.

## Problem

`tk` gets most of its value when agents keep the native command shape and the hook/router
transparently routes noisy commands through compact filters:

- `dotnet build` -> `tk dotnet build`
- `dotnet test` -> `tk dotnet test`
- `git status` -> `tk git status`
- `git log` -> `tk git log`

The performance gap showed up when agents, especially subagents, bundled work into one Bash
request:

```bash
cd src && dotnet build | head -50
git status && echo "---DIFF---" && git diff | head -100
find . -type f | head -60
grep -r "Foo" src | head -50
```

The older hook policy only routed safe single commands. Once a command contained a pipe, chain
operator, newline, or substitution, the hook left it unchanged. That preserved shell semantics,
but it meant the high-volume cases bypassed `tk` completely.

## What we inspected

Local `tk` files:

- `hooks/tk-route.sh`
- `hooks/README.md`
- `Modules/ModuleCatalog.cs`
- `Stats/bash_opportunities.py`
- `Stats/result/model.json`
- `Tk.Tests/hooks/test-tk-route.sh`

Local `rtk` comparison:

- `/Users/artsiom/Developer/rtk/src/discover/registry.rs`
- `/Users/artsiom/Developer/rtk/src/discover/lexer.rs`
- `/Users/artsiom/Developer/rtk/src/discover/rules.rs`
- `/Users/artsiom/Developer/rtk/src/hooks/hook_cmd.rs`
- `/Users/artsiom/Developer/rtk/src/hooks/rewrite_cmd.rs`
- `/Users/artsiom/Developer/rtk/src/hooks/permissions.rs`

## Stats work done

`Stats/bash_opportunities.py` was extended to make this visible:

- `--main-only` / `--subagents-only`
- `--event-level`
- `--compound`
- JSON fields for event totals, segment totals, shown chars, compound breakdowns, and examples

The key point in the analyzer is event-level accounting: shown chars are counted once per Bash
event, not once per segment. Segment-level accounting is still useful for finding commands inside
compound scripts.

## Findings

The useful prioritization slice is `0.6.0`, because the latest `0.7.0` slice was too small
(`28` subagent Bash events, `26` compound). For `0.6.0`, subagents-only:

```text
bash_events=2648
compound_events=2087
compound_shown_chars=2408614
```

Top compound patterns:

| Pattern | Events | Shown chars |
|---|---:|---:|
| `pipeline` | 1314 | 1645619 |
| `multi_line_script` | 944 | 1077018 |
| `pipe_to_grep` | 480 | 612545 |
| `pipe_to_head` | 549 | 668483 |
| `chain_and` | 374 | 361264 |
| `chain_semicolon` | 327 | 335763 |
| `pipe_to_tail` | 264 | 305764 |
| `pipe_to_sort` | 99 | 235535 |
| `pipe_to_wc` | 41 | 35384 |

Top commands inside subagent compound commands:

| Command | Segments | Events | Routing implication |
|---|---:|---:|---|
| `grep` | 1231 | 752 | Biggest bucket; mostly nudge/manual, not blind rewrite |
| `cd` | 757 | 752 | Shell glue; preserve |
| `head` | 590 | 553 | Pipeline modifier; preserve |
| `echo` | 373 | 291 | Shell glue; preserve |
| `find` | 335 | 300 | Good candidate for conservative routing |
| `tail` | 294 | 264 | Pipeline modifier / file read; preserve |
| `cat` | 197 | 156 | Native read; preserve |
| `ls` | 131 | 113 | Good candidate for conservative routing |
| `sort` | 112 | 99 | Pipeline modifier; preserve |
| `dotnet test` | 93 | 93 | Good routing candidate |
| `dotnet build` | 72 | 72 | Good routing candidate |
| `git status` | 70 | 65 | Good routing candidate |
| `git diff` | 42 | 38 | Keep raw/manual; patch fidelity matters |
| `git log` | 20 | 19 | Good routing candidate |
| `git show` | 15 | 13 | Keep raw/manual; patch fidelity matters |

Representative examples:

```bash
ls -la /Users/artsiom/Developer/_/ | head -50
du -sh /Users/artsiom/Developer/_/www/* 2>/dev/null | sort -rh | head -30
find /Users/artsiom/Developer/_/www -size +0c -type f 2>/dev/null | head -60
find /Users/artsiom/Developer/_/www -size +0c -type f 2>/dev/null | wc -l
git status && echo "---BRANCH---" && git branch --show-current && echo "---DIFF---" && git diff hooks/tk-route.sh | head -100
```

## rtk comparison

`rtk` does not solve this by owning the shell. It solves part of it with a quote-aware rewrite
layer:

- rewrites supported segments inside `&&`, `||`, `;`, and background `&`
- rewrites the left side of pipe groups
- preserves `cd`, `echo`, and other shell glue
- preserves env prefixes in supported cases
- avoids or gates risky constructs like substitutions, heredocs, and file-target redirects

Example behavior:

```bash
git status && echo done
# -> rtk git status && echo done

cd x && dotnet build | head -5
# -> cd x && rtk dotnet build | head -5

git log | head -5 && git stash
# -> rtk git log | head -5 && rtk git stash
```

Important limitation: this is not a full Bash parser. It does not fully cover plain multiline
scripts, pipe RHS commands, substitutions, heredocs, or all redirection shapes.

The useful idea to copy was not `rtk`'s entire command surface. It was the smaller hook-layer idea:
a quote-aware segment rewrite before falling back to passthrough.

## Implemented in tk

The hook now has a conservative compound rewrite path in `hooks/tk-route.sh`.

Safe single commands still keep the existing behavior:

- `dotnet build|test|restore` -> `tk dotnet build|test|restore`
- `git status|log` -> `tk git status|log`
- other safe single commands may fall back to `rtk rewrite`
- `git diff|show` stay raw

Compound commands now get segment-aware `tk` rewrites for the conservative built-in set only:

```bash
git status && echo done
# -> tk git status && echo done

cd x && dotnet build | head -50
# -> cd x && tk dotnet build | head -50

git log | head -5 && git status
# -> tk git log | head -5 && tk git status

git diff | head -20
# unchanged
```

The compound path deliberately does not use `rtk` fallback. Many `rtk` generic rewrites are not
obviously safe inside pipelines or larger shell expressions.

Current deliberate gaps:

- env-prefix commands such as `FOO=bar dotnet build && echo done` are unchanged
- embedded newlines are unchanged
- command substitutions are unchanged
- `git diff` / `git show` remain unchanged even inside compounds
- `grep` / `rg` are nudged or left manual rather than blindly rewritten

## Why not route every tk command

Automatic routing is only safe when the native command intent is preserved.

Good examples:

- `dotnet build` -> `tk dotnet build`
- `git status` -> `tk git status`

Risky examples:

- `rg Foo` is not exactly `tk focus Foo`; `focus` is ranked orientation, not exact grep.
- `cat file` / `sed -n` are exact reads; `tk view` is a structure map/orientation tool.
- `find` / `ls` / `tree` can be routed only for simple overview patterns, not every option shape.
- `git status && git diff` could become `tk changes`, but that is a macro substitution, not the
  same command.
- `tk def` / `tk refs` / `tk callers` have no native shell equivalent. They should be advertised
  and nudged, not inferred from arbitrary grep in all cases.

The default policy should stay conservative: route when the native command shape maps directly to
`tk <same command>`, nudge when the model is probably using the wrong tool, and leave exact/raw
commands alone when content fidelity matters.

## Priorities from the data

1. `grep` / `rg` in compounds.
   This is the largest bucket. Do not blindly rewrite all grep. Improve symbol-search nudges and
   consider a conservative broad-search route only where `tk grep` semantics are known to match.

2. `find` / `ls` in compounds.
   These are good next auto-route candidates, especially simple forms followed by `| head` or
   `| wc`. The stats show high usage and mostly orientation intent.

3. Pipeline modifiers.
   Treat `head`, `tail`, `sort`, and `wc` as modifiers, not standalone routing targets. The hook
   should preserve them while routing safe left-side commands.

4. Existing `dotnet` / `git status` / `git log` routing.
   This is already implemented and useful, but it is not the dominant subagent volume.

5. `git diff` / `git show`.
   Keep raw by default. These are tempting because they appear in compounds, but compaction can
   hide changed lines or patch context.

## Backlog

### Near term

- Update `hooks/README.md` to describe the new compound rewrite behavior.
- Add explicit tests for:
  - `||` chains
  - `git diff && git status`
  - quoted operators inside compounds
  - command substitutions staying passthrough
  - env-prefix commands staying passthrough
- Consider simple `find` / `ls` compound routes after defining exact supported option shapes.
- Re-run stats after using the new hook to measure whether compound bypasses drop.

### Later

- Decide whether `tk grep` is semantically close enough for selected broad grep routes.
- Improve symbol grep nudges using more examples from subagent transcripts.
- Consider a `tk shell --b64` + PATH-shim prototype only if segment rewrite leaves a large
  unresolved bypass class.
- Consider `du` / size-summary support if the `du -sh ... | sort | head` pattern keeps showing up.

## Verification commands used

Hook implementation was verified with:

```bash
bash -n hooks/tk-route.sh
bash -n Tk.Tests/hooks/test-tk-route.sh
Tk.Tests/hooks/test-tk-route.sh
tk dotnet test Tk.Tests/Tk.Tests.csproj --filter HookRouteScriptTests
git diff --check -- hooks/tk-route.sh Tk.Tests/hooks/test-tk-route.sh
```

Stats were inspected with:

```bash
python3 Stats/bash_opportunities.py --version 0.6.0 --event-level --compound --subagents-only --top 30
python3 Stats/bash_opportunities.py --version 0.6.0 --event-level --compound --subagents-only --json --examples 5
python3 Stats/bash_opportunities.py --version 0.7.0 --event-level --compound --subagents-only --top 15 --examples 5
```
