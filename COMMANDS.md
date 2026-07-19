# Command reference

Prefix any command with `tk`. If a filter exists, output is compressed. If not, the command passes through unchanged — so `tk` is always safe to use.

Two global output controls work with any command:

```bash
tk --more <command>      # same command, but with more detail
tk --raw <command>       # run with original unfiltered output
```

## .NET (`dotnet` module)

```bash
tk dotnet build          # compact build summary: ok/FAIL + p/e/w/t
tk dotnet test           # compact test summary: pass/fail/skip/t
tk dotnet restore        # compact restore summary: up/p/e/nu/t

tk quality [path]                      # local analyzers + dotnet build + dotnet format --verify-no-changes
tk quality [path] --test-filter <expr> # also run tests matching a filter
tk quality [path] --no-build           # skip the build step
tk quality [path] --no-format          # skip the format-verify step
tk quality [path] --test / --deep      # also run the full test suite
tk quality [path] --all                # show every error/warning/format issue, not just changed-file ones
```

## Git (`core`)

```bash
tk git status            # count-first status summary: staged/modified/untracked + all paths
tk git log                # one-line-per-commit format, max 30
tk git diff               # summary-first diff: file stats, top files, key changed lines
tk git show                # same as diff
tk changes                # compact repo state card: status + diff, for agent startup
tk branch [base]           # ahead/behind vs an auto-detected or explicit base branch, with commit list
tk --more git diff        # more hunks and changed lines
tk --raw git diff         # original git diff output
```

Any other git subcommand (`add`, `commit`, `push`, `pull`, `fetch`, `stash`, `checkout`, `merge`, `rebase`, `reset`, `tag`, ...) is a clean, unfiltered passthrough to the real `git`.

## Repo navigation (`core`)

```bash
tk tree                  # shallow repo tree with directory/file counts
tk tree --code           # code-focused tree: trims docs/assets/generated noise
tk files                 # compact key-file inventory
tk files --code          # code-focused inventory: entrypoints/interfaces/configs first
tk files --changed       # changed files only
tk files --ext cs        # files filtered by extension
tk ls <path>              # directory listing, names only (no perms/dates/sizes)
tk focus CommandRunner            # code-first repo search with top files + samples
tk focus auth token refresh       # OR sweep over words, ranked by term coverage
tk focus CommandRunner --code     # strict code-only search
tk focus CommandRunner -p tk --files-only  # scope to a path, only likely files
tk focus Refit --docs             # docs/specs/guides only
tk focus Refit --all              # include docs/logs/other in the ranking
tk focus "RunAsync(" -p tk        # quote for an exact phrase, scoped to a path
tk grep [flags] pat path   # grep, summarized: match count, top files, samples
tk rg [flags] pat path      # same, via ripgrep
tk find <path> [flags]      # find with path prefix stripped, count-first summary
```

## File editing

```bash
tk write <file> <start>[-<end>] --expect-first <text>   # replace lines start..end with stdin content
tk write <file> <start>[-<end>] --expect-last <text>     # anchor on the current last line instead
tk write <file> <start>[-<end>] --expect-sha <hex>       # anchor on the file's current sha256 prefix
```

`end == start-1` inserts before `start` (nothing removed); `(n+1)-(n)` appends at EOF. At least one `--expect-*` anchor is required — the write is refused if the file's current content doesn't match, so stale line numbers can't silently clobber content.

## Unity (`unity` module, opt-in — never changes the plain commands above)

```bash
tk unity tree            # tree that hides Library/Temp/Logs and .meta sidecars
tk unity files           # file inventory aware of Unity dirs and code extensions (.shader/.asmdef/...)
tk unity tree --code     # code-focused, keeps Assets/ as the source root
tk unity status          # git status that folds .meta noise into a meta= count
```

## File moves (`core`, always available)

```bash
tk mv src/OldName.cs src/NewName.cs  # plain filesystem move (never touches the git index); git detects the rename itself on the next status/diff
tk mv src/OldName.cs src/            # moves into directory, destination = src/OldName.cs
tk mv src/OldName.cs src/NewName.cs --no-fix-ns   # skip automatic namespace fix; move only
tk mv src/OldName.cs src/NewName.cs --ns Foo.Bar  # use this namespace instead of the computed convention
```

For `.cs` files moved across folders, `tk mv` fixes the namespace and referencing files by default (needs the `lsp` module).

## Semantic navigation (`lsp` module, opt-in)

`tk` started as a read-only proxy, but text filtering only covers half of what burns an agent's context — the other half is editing. When an agent renames a symbol or moves a file without semantic tooling, it often rewrites the file from scratch, re-emitting hundreds of lines into the context and losing git history. The `lsp` module replaces that with real semantic operations, editing files in place:

```bash
tk refs src/Mediator.cs:7:18         # all references to the symbol at file:line:col
tk refs IBus                         # or resolve a name to its references
tk def IBus                          # where a symbol is defined
tk def src/Mediator.cs:7:18          # go to definition at position
tk impl IBus                         # who implements/overrides it
tk impl src/Mediator.cs:7:18         # implementations at position
tk callers Send                      # who calls it (incoming call hierarchy)
tk callers src/Mediator.cs:7:18      # incoming callers at position
tk calls Send                        # what it calls (outgoing call hierarchy)
tk sig Send                          # signature + doc-comment summary (hover)
tk sym Media                         # fuzzy workspace-wide symbol search
tk diag src/Mediator.cs              # compile diagnostics, no build
tk diag src/Mediator.cs --errors     # errors only
tk diag --changed                    # diagnostics for every changed .cs file (staged+modified+untracked)
tk fix src/Mediator.cs               # add missing using / remove unnecessary using only
tk rename src/Mediator.cs:7:18 IBus  # rename a symbol everywhere, editing files IN PLACE
tk lsp status                        # warm-daemon status for the current workspace
tk lsp stop                          # stop the workspace daemon
```

Notes:

- `tk calls` depends on the language server implementing `callHierarchy/outgoingCalls`; some Roslyn builds don't, and answer with an empty result indistinguishable from "genuinely calls nothing" — `tk calls` prints a note when that happens rather than asserting either way.
- `tk fix` applies only the safe subset of `textDocument/codeAction` (add/remove a `using`); it reports "unsupported by server" rather than applying anything wider.
- A per-workspace daemon hosts the language server and stays warm across calls; the first call pays a cold load, later calls are fast. Edits made on disk between calls are picked up automatically.
- **Requirement (C#):** these commands need a C# language server. `tk` discovers it in this order: `TK_LSP_CSHARP_SERVER` env var (explicit path), the Roslyn server bundled with the **C# Dev Kit / C# extension for VS Code** (`~/.vscode/extensions/ms-dotnettools.csharp-*/.roslyn/...`), or `csharp-ls` on `PATH`. If none is found, `tk refs`/`tk rename` report the server is unavailable with an install hint; the rest of `tk` is unaffected.

## Modules

```bash
tk module list                   # show modules and enabled state
tk module disable unity          # drop a group's commands + its init snippet
tk module enable lsp
```

Config lives at `~/.local/state/tk/modules` (or `$XDG_STATE_HOME/tk/modules`); with no config file, all modules are enabled. The set of enabled modules determines which instruction snippets `tk init` writes into `~/.claude/CLAUDE.md`, `~/.codex/AGENTS.md`, and `~/.config/opencode/AGENTS.md`.

```bash
tk init   # (re)install the instruction blocks above; marker-based and idempotent
```

## File reading

```bash
tk view src/Program.cs           # compact file card: line count, symbols, hot ranges
tk view src/Program.cs:40-90      # numbered exact range
tk view src/Program.cs::MyMethod # symbol body by name
tk view src/Program.cs --symbols # symbols only
tk --raw view src/Program.cs     # full file with numbered lines
```

## Service logs (ASP.NET / Kestrel / MassTransit)

```bash
tk log app.log           # strip startup noise, dedup, keep warnings+errors
tk --more log app.log     # every deduped info group, not just the top ones
tk log app.log --errors  # errors/critical only
tk log app.log --last 20 # last 20 entries
tk log app.log --all     # raw output, no filtering
```

## Passthrough

Any command tk has no filter for runs as-is:

```bash
tk cargo build
tk npm test
```

## Filters

| Filter | Trigger | What it does |
|--------|---------|-------------|
| **DotnetBuild** | `tk dotnet build` | Every distinct error site shown (no cap, no file truncation, full message); warnings grouped/capped; NuGet vulns deduplicated; project count and duration |
| **DotnetTest** | `tk dotnet test` | Pass/fail/skip counts; on failure, all failed test names listed with full assertion diff and stack trace per test |
| **DotnetRestore** | `tk dotnet restore` | Counts restored projects, surfaces errors, collapses NuGet noise |
| **GitStatus** | `tk git status` | Count-first status summary with staged/modified/untracked counts, branch, and all changed paths (complete set, never capped) |
| **GitLog** | `tk git log` | Converts full-format log to one-line-per-commit, caps at 30 |
| **GitDiff** | `tk git diff`, `tk git show` | Summary-first diff: all changed files listed with stats (complete set); hunk preview includes context lines around changes; large diffs cap hunk preview with an explicit overflow note |
| **Changes** | `tk changes` | Compact repo state card combining `git status` and `git diff` |
| **Find** | `tk find <path> [flags]` | Count-first find summary with top groups and a few representative paths |
| **Grep** | `tk grep`/`tk rg [flags] pat path` | Grep/ripgrep summary: match count, top files, samples |
| **View** | `tk view <file[:a-b|::sym]>` | Compact file card with symbols/hot ranges, exact numbered line ranges, or a symbol's body |
| **Tree** | `tk tree [path]` | Shallow repo tree with directory/file counts |
| **Files** | `tk files [path]` | Compact inventory of key files, optional extension or changed-file filtering |
| **Focus** | `tk focus <word...> [-p path]` | Multi-word OR search ranked by term coverage; quote for an exact phrase; compact summary output |
| **LogFile** | `tk log <file>` | Parses ASP.NET-style logs: strips startup/framework noise, collapses HTTP request pairs, deduplicates repeated entries (count shown), preserves full stack traces for kept errors |

Any git subcommand other than `status`/`diff`/`show`/`log` (`add`, `commit`, `push`, `pull`, ...) is unfiltered passthrough, not covered by a filter above.

Unrecognized commands pass through without modification.
