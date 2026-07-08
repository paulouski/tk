# Agent tooling backlog 2

Status: 2026-07. This is the second-pass backlog after the hook-routing work,
read-cost/edit-cost stats, the openslimedit research pass, and the discussion
about large `Read` calls and LSP-first file orientation.

This document intentionally supersedes the priority order in
`docs/agent-tooling-backlog.md` where that older file reflects pre-implementation
ideas. Keep the older backlog as raw context; use this file as the current
execution order.

## Current problem statement

The default productive flow is:

- main dialog as smart orchestrator;
- subagents as executors;
- `tk` as a mechanical safety net and compact-output layer.

The highest-impact work is therefore not "teach the main model one more command".
It is making subagents use better tools even when they do not read or follow the
root instructions well. Hook-level behavior beats prompt-level advice whenever
the same result can be enforced mechanically.

The current data-backed issues:

- subagents dominate command and read volume;
- compound Bash was the largest routing bypass;
- large uncapped `Read` calls can erase savings from many compact commands;
- repeated full reads exist, but strict same-session repeat full reads are not a
  large enough bucket to justify hard blocking;
- `.cs` files should usually be explored through LSP or an outline before full
  body reads.

## Already done in the current working tree

These are treated as landed for planning purposes, but still need normal review
before commit:

- compound Bash routing in `hooks/tk-route.sh`;
- three-tier downstream guard:
  - display-only RHS keeps pipeline and routes safe LHS;
  - dotnet `build|test|restore` piped into problem-shaped grep drops the RHS and
    routes to `tk dotnet ...` with a hint;
  - arbitrary content filters stay passthrough;
- bare newline handling for simple multi-command scripts;
- env-prefix preservation for routed commands;
- nudge throttle keyed by `session_id`;
- `tk-agent-preamble.sh` for `Task` subagent prompts;
- `Stats/bash_opportunities.py` grep classifier;
- `Stats/bash_opportunities.py --edit-cost`;
- `Stats/bash_opportunities.py --read-cost`;
- `Read` auto-cap: whole-file `Read` over 500 lines gets `limit: 500`;
- subagent preamble line about reading large files in slices.

## S tier

### 1. Make `tk view` C# semantic-outline first

Current `tk view` is still mostly a regex-era map. Its `hot` section is not
actually "hot"; it is the first few approximate symbol ranges. That is weak
guidance for large `.cs` files.

Replace the default behavior for `.cs` files with an LSP-backed semantic outline:

```text
view LspDaemon.cs lines=1474 source=lsp symbols=37
class LspDaemon 27-1474 body=1448
  LspDaemon(...) 42-57 body=16
  RunAsync(CancellationToken cancellationToken) 76-183 body=108
  HandleMessageAsync(...) 185-260 body=76
hid=22/37 symbols (--more)
```

Required behavior:

- show kind, name, signature when available, start-end range, body size;
- use `textDocument/documentSymbol` if available;
- use LSP ranges for C# before regex;
- default output is outline only, not method bodies;
- preserve `tk view file:line-end` and `tk view file::symbol` as exact body/range
  renderers;
- if LSP is unavailable, fall back to current regex behavior and mark it
  `source=regex approx`;
- rename `hot` to `ranges` or remove it for the C# path.

Why this is S tier:

- it gives the `Read` cap hint somewhere useful to send the agent;
- it does not require the agent to already know the symbol name;
- it directly targets the most common bad pattern: "read the whole large C#
  file to find the method".

Suggested validation:

- unit tests for class/method/property/nested symbol outlines;
- one integration test on a real large `.cs` file;
- verify that `tk view Big.cs` plus sliced `Read` is enough to inspect one method.

### 2. C#-specific large-Read hint

Keep the existing 500-line cap. Improve the hint for large `.cs` files:

```text
Read capped at 500 lines by tk-route (file has N lines). For C# files,
run `tk view <file>` to get method ranges, then read the needed offset/limit
slice. If you already know the symbol, use `tk def|refs|callers|impl`.
Re-run with explicit limit=N only when reviewing the whole file.
```

Do not auto-rewrite `Read` into `tk view`. The hook should cap and guide; the
agent should choose the next tool call.

### 3. Measure post-hook adoption

Add a stats pass for "after hook intervention, what happened next".

Useful questions:

- after `cap-read`, did the next 3-5 calls include `tk view`, `tk def`, `tk refs`,
  `tk callers`, or sliced `Read`?
- after problem-shaped pipeline rewrite, did the agent rerun `--raw` often?
- after `tk-agent-preamble`, did subagents use more `tk` commands per session?
- did compound bypasses drop in the next 0.7+ data slice?

This should be transcript analytics, not runtime overhead.

## A tier

### 4. YAML/workflow outline for `tk view`

The `cicd.yml` example is the non-C# version of the same problem: a large
workflow file is read in full because there is no compact structure view.

Add a conservative outline for YAML, especially GitHub Actions workflows:

```text
view cicd.yml lines=1569 source=yaml
workflow: CI
on: push, pull_request
jobs:
  build 42-180 steps=9
  test 182-410 steps=14
  deploy 900-1210 steps=11
```

Start narrow:

- top-level keys;
- for `.github/workflows/*.yml`, `jobs.<name>` ranges and step counts;
- no semantic validation;
- no attempt to hide that YAML anchors/templates can complicate ranges.

Why A tier:

- large config reads are expensive;
- `tk def` cannot help;
- a simple outline gives the agent a slice target.

### 5. Smart edit experiment, not default edit replacement

The edit-cost stats justify an experiment: old_string payloads are a real output
cost, and most of that cost sits in subagents.

Keep the scope explicit:

```text
tk patch apply
[src/Foo.cs sha256:<file-hash>]
replace 57-59:
  new content
```

Rules:

- explicit command only; do not transparently intercept provider-native `Edit`;
- line ranges plus stale-file guard;
- no hash tags in normal file reads;
- on mismatch, fail closed and tell the agent to reread the slice;
- benchmark before/after with raw tokens, time, and success rate.

Reasoning:

- openslimedit-like line-range edit is relevant;
- hashline reads are not: they add prompt noise and can make models loop;
- this is promising for implementer subagents, but should not become a default
  editing grammar until measured.

### 6. Terse agent-facing text pass

Reduce repeated prompt and hint cost:

- hook `additionalContext`;
- `tk-agent-preamble.sh`;
- `tk init` snippets;
- noisy success messages for mutating `tk` commands;
- help text that agents see often.

Keep the content exact. The goal is shorter guidance, not clever copy.

## B tier

### 7. Conservative `find` / `ls` compound routes

Only after new post-hook stats show these are still high volume.

Good candidates:

- simple orientation forms followed by `head`;
- simple count forms followed by `wc -l`;
- no destructive commands;
- no exact-content filters where shell semantics matter.

Possible target is `tk view <dir>` once directory cards exist. Do not route to a
new command before the target output is clearly better.

### 8. Directory/project cards

Useful, but lower than C# semantic outline and YAML workflow outline.

Candidate outputs:

- `tk view <dir>`: repo/dir card, important files, project markers, test dirs;
- `tk view <project.csproj>`: target framework, package refs, project refs;
- `tk view <solution.sln>`: apps/libs/tests and project graph.

Keep this as orientation only. Exact reads remain native `Read` slices.

### 9. Targeted test suggestions

Prefer suggest-only first:

```bash
tk test suggest
```

Output should explain:

- changed files;
- likely test projects;
- suggested `dotnet test --filter ...` commands.

Do not auto-run or hide full validation yet.

## Parked

### AST/structural rewrite

Keep as a later feature for broad mechanical migrations. It needs preview,
atomicity, and good tests. Do not start it until transcript evidence shows
repeated manual structural rewrites.

### DAP/debugger integration

Potentially valuable, but large surface area. Park until debugging transcripts
show repeated edit-run-print loops.

### `tk shell --b64` and PATH shims

Keep only as an escape hatch if segment rewrite leaves a large unresolved bypass
class. Current hook work should be measured first.

## Drop or keep out of tk

Drop from active backlog:

- generic preview/accept grammar for everything; use command-specific preview
  where needed;
- NuGet intelligence until package work becomes frequent;
- generic persistent eval worker;
- in-process shell/search runtime;
- rule-trigger framework beyond narrow hook nudges;
- project memory as a `tk` feature;
- checkpoint/rewind/context pruning;
- typed subagent orchestration or cheap-model role management.

Reason: these drift into harness/provider territory. `tk` should remain a
utility layer with compact commands, semantic repo helpers, and mechanical hooks.

## Proposed execution order

1. Review and commit the current hook/stats/read-cap working tree.
2. Implement LSP-backed C# `tk view` semantic outline.
3. Update large `.cs` `Read` hint to point to `tk view <file>` first.
4. Add post-hook adoption stats.
5. Add YAML/GitHub Actions outline for `tk view`.
6. Run a fresh data slice and compare:
   - compound bypasses;
   - full reads over 500 lines;
   - sliced-read follow-up rate;
   - subagent `tk` usage after preamble injection.
7. Decide whether smart edit gets a prototype benchmark.
8. Only then revisit `find`/`ls` routes and project cards.

## Verification policy

For each item:

- start with transcript stats or a concrete failing transcript example;
- add focused tests before behavior changes where practical;
- run the smallest relevant checks;
- compare before/after stats on the same version slice when changing analytics;
- keep runtime hooks deterministic and silent on failure;
- do not route commands when compact output would answer a different question.
