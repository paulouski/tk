# tk — Token Killer

A CLI proxy between AI coding agents (Claude Code, Codex, Cursor) and your dev tools. It runs your command, strips the noise from the output — build logs, git verbosity, NuGet warnings, startup spam — and hands the agent only what it needs to act.

Result: **60–99% fewer tokens** per command, which means faster responses, cheaper API calls, and more room in the context window for actual work.

Built by a .NET developer for .NET work, so the deepest filters target the C#/.NET toolchain (builds, restore, semantic refactors). But most of `tk` — git, repo navigation, file reading, search, log triage — is language-agnostic and useful in any repo. The design is one filter per command, so growing into other ecosystems later is a matter of adding filters, not reworking the tool.

## Why

An agent runs `dotnet build` and gets back 200 lines. 195 of them are NuGet restore messages, MSBuild banners, and duplicated vulnerability warnings. The agent reads all of it, burning tokens on noise. `tk` reduces that to 2–3 lines with just the outcome, errors, and warnings.

Same idea for `git status`, `git diff`, `git log`, file reading, repo search, and service log files.

## Install

Requires the [.NET 8 **Runtime**](https://dotnet.microsoft.com/download/dotnet/8) (not the SDK). The install script will offer to install it for you (via `winget` on Windows, Homebrew on macOS) if it is missing.

### Windows

```powershell
irm https://raw.githubusercontent.com/paulouski/tk/main/install.ps1 | iex
```

Downloads the latest `tk.exe` from GitHub Releases into `%LOCALAPPDATA%\tk` and adds it to your user PATH.

### macOS (Apple Silicon)

```bash
curl -fsSL https://raw.githubusercontent.com/paulouski/tk/main/install.sh | bash
```

Downloads the latest `tk-osx-arm64` binary into `~/.local/bin/tk` and adds it to your PATH.

Both installers also install global instruction blocks to:

- `~/.claude/CLAUDE.md`
- `~/.codex/AGENTS.md`

Re-run the same command at any time to update to the latest release.

### Build from source (development)

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8).

```powershell
git clone https://github.com/paulouski/tk.git
cd tk
.\build.ps1
```

`build.ps1` compiles and publishes the binary to `%LOCALAPPDATA%\tk`, adds it to user PATH, and runs `tk init`.

You can also publish manually:

```bash
dotnet publish Tk.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o <your-bin-dir>
# add <your-bin-dir> to PATH
tk init   # optional: install global Claude + AGENTS instructions
```

## Usage

Prefix any command with `tk`. If a filter exists, output is compressed. If not, the command passes through unchanged — so `tk` is always safe to use.

`tk` also supports two global output controls:

```bash
tk --more <command>      # same command, but with more detail
tk --raw <command>       # run with original unfiltered output
```

```bash
# .NET
tk dotnet build          # compact build summary: ok/FAIL + p/e/w/t
tk dotnet test           # compact test summary: pass/fail/skip/t
tk dotnet restore        # compact restore summary: up/p/e/nu/t

# Git
tk git status            # count-first status summary: staged/modified/untracked + top paths
tk git log               # one-line-per-commit format, max 30
tk git diff              # summary-first diff: file stats, top files, key changed lines
tk git show              # same as diff
tk changes               # compact repo state card: status + diff
tk --more git diff       # more hunks and changed lines
tk --raw git diff        # original git diff output
tk git push              # ultra-compact (keep first 3 + last 2 lines)
tk git commit            # ultra-compact
# ... any other git subcommand gets compact treatment or passthrough

# Repo navigation
tk tree                  # shallow repo tree with directory/file counts
tk tree --code           # code-focused tree: trims docs/assets/generated noise
tk files                 # compact key-file inventory
tk files --code          # code-focused inventory: entrypoints/interfaces/configs first
tk files --changed       # changed files only
tk files --ext cs        # files filtered by extension
tk focus CommandRunner            # code-first repo search with top files + samples
tk focus auth token refresh       # OR sweep over words, ranked by term coverage
tk focus CommandRunner --code     # strict code-only search
tk focus CommandRunner -p tk --files-only  # scope to a path, only likely files
tk focus Refit --docs             # docs/specs/guides only
tk focus Refit --all              # include docs/logs/other in the ranking
tk focus "RunAsync(" -p tk        # quote for an exact phrase, scoped to a path

# Unity (explicit, opt-in — never changes the plain commands above)
tk unity tree            # tree that hides Library/Temp/Logs and .meta sidecars
tk unity files           # file inventory aware of Unity dirs and code extensions (.shader/.asmdef/...)
tk unity tree --code     # code-focused, keeps Assets/ as the source root
tk unity status          # git status that folds .meta noise into a meta= count

# File moves (always available)
tk mv src/OldName.cs src/NewName.cs  # git mv when tracked (preserves history), else filesystem move
tk mv src/OldName.cs src/            # moves into directory, destination = src/OldName.cs

# Semantic navigation (LSP-backed, opt-in module)
tk refs src/Mediator.cs:7:18         # all references to the symbol at file:line:col
tk rename src/Mediator.cs:7:18 IBus  # rename a symbol everywhere, editing files IN PLACE
tk lsp status                        # warm-daemon status for the current workspace
tk lsp stop                          # stop the workspace daemon

# Modules (enable/disable feature groups)
tk module list                   # show modules and enabled state
tk module disable unity          # drop a group's commands + its init snippet
tk module enable lsp

# Quality gate
tk quality .                     # dotnet build + dotnet format verify
tk quality . --test-filter Onboarding  # add targeted tests

# File reading
tk view src/Program.cs           # compact file card: line count, symbols, hot ranges
tk view src/Program.cs:40-90     # numbered exact range
tk view src/Program.cs --symbols # symbols only
tk --raw view src/Program.cs     # full file with numbered lines

# Service logs (ASP.NET / Kestrel / MassTransit)
tk log app.log           # strip startup noise, dedup, keep warnings+errors
tk log app.log --errors  # errors/critical only
tk log app.log --last 20 # last 20 entries
tk log app.log --all     # raw output, no filtering

# Anything else — passthrough
tk cargo build           # no filter exists, runs as-is
tk npm test              # same — passthrough
```

### Output format

Filtered output is compact and outcome-first: a short summary line in stable `key=value` fields, details only after it. Common keys: `p`=projects, `e`=errors, `w`=warnings, `t`=time, `pass/fail/skip`=tests, `nu`=NuGet vulnerabilities, `st/mod/untr`=staged/modified/untracked, `f`=files, `m`=matches. The format is meant to be cheap for an agent to parse and easy to scan. When a parser is unsure, `tk` falls back to a short raw tail rather than guessing. Escalate detail with `--more`, get the original output with `--raw`. When lines are dropped by filtering, the output ends with a footer such as `hid=42/200 (--more, --raw)` showing how many lines were hidden and the escape hatches available; at `--more` level only `(--raw)` is shown.

## Semantic navigation (`tk refs` / `tk rename` / `tk mv`)

`tk` started as a read-only proxy, but text filtering only covers half of what burns an agent's context. The other half is editing. When an agent renames a symbol or moves a file without semantic tooling, it often rewrites the file from scratch (or deletes and recreates it) — re-emitting hundreds of lines into the context, losing git history, and risking unrelated changes. `tk mv` and the `lsp` module replace that with real semantic operations:

- `tk mv <old> <new>` — move a file **preserving git history** (`git mv` when tracked, filesystem move otherwise), so it shows up as a rename instead of a delete+add. Never delete and recreate a file to rename or relocate it — use this instead, then fix any namespace/reference fallout the compiler flags (a one-line edit, not a full-file rewrite).
- `tk refs <file:line:col>` — exact references to a symbol (declaration + all usages), grouped by file. Accurate, instead of guessed from grep.
- `tk rename <file:line:col> <newName>` — rename a symbol across the whole workspace, applying edits **in place** (files are modified, not recreated), so git history and unrelated lines are preserved. Intended for refactoring existing code, not atomic single-file micro-edits.

A per-workspace daemon hosts the language server and stays warm across calls; the first call pays a cold load, later calls are fast. Edits made on disk between calls are picked up automatically. `tk lsp status` / `tk lsp stop` inspect and control it.

**Requirement (C#):** these commands need a C# language server. `tk` discovers it in this order:

1. `TK_LSP_CSHARP_SERVER` env var (explicit path),
2. the Roslyn server bundled with the **C# Dev Kit / C# extension for VS Code** (`~/.vscode/extensions/ms-dotnettools.csharp-*/.roslyn/...`),
3. `csharp-ls` on `PATH`.

If none is found, `tk refs` / `tk rename` report that the server is unavailable with an install hint; the rest of `tk` is unaffected. `tk` does not download the server itself.

## Modules

`tk` is organized into feature groups so you only enable what you use:

- `core` — always on (navigation, search, view, git, `init`, `module`, …)
- `dotnet` — `tk quality`, `tk dotnet build|test|restore`
- `unity` — `tk unity tree|files|status`
- `lsp` — `tk refs`, `tk rename`, `tk lsp …`

`tk module list|enable <name>|disable <name>` toggles a group. The set of enabled modules also determines which instruction snippets `tk init` writes into `~/.claude/CLAUDE.md` and `~/.codex/AGENTS.md`, so disabling a module both removes its commands and stops advertising it to your agents. Config lives at `~/.local/state/tk/modules` (or `$XDG_STATE_HOME/tk/modules`); with no config file, all modules are enabled.

## Configuring with Claude Code

```bash
tk init
```

This installs or updates global tk instruction blocks in:

- `~/.claude/CLAUDE.md`
- `~/.codex/AGENTS.md`

The installer is marker-based and idempotent: rerunning `tk init` updates the tk block instead of duplicating it.

## Token savings

These are noise-reduction figures. `tk` is completeness-first: it drops irrelevant things (framework banners, restore spam, duplicate messages) but never silently omits relevant things (changed files, error sites, test failure detail, stack frames). Actual savings depend on noise ratio.

| Command | Typical output | After tk | Savings (noise dropped) |
|---------|---------------|----------|---------|
| `dotnet build` (success, 12 projects) | ~180 lines | 1 line | ~99% |
| `dotnet build` (3 errors, 5 warnings) | ~180 lines | ~8-10 lines | ~94% |
| `dotnet test` (all pass) | ~60 lines | 1 line | ~98% |
| `dotnet test` (failures) | varies | full failure detail per test | noise dropped, signal kept |
| `git status` (15 files) | ~30 lines | ~16 lines (all paths) | ~45% |
| `git diff` (10 files, 500 lines) | 500 lines | summary + all files listed + hunk preview | depends on diff size |
| `changes` (status + diff startup check) | ~530 lines | ~3-15 lines | ~97% |
| `tree` (medium repo) | ~100-500 lines | ~5-15 lines | ~90-98% |
| `focus` (multi-word OR search) | ~50-500 lines | ~3-10 lines | ~85-98% |
| `git log` (full format) | ~200 lines | ~30 lines | ~85% |
| `view Program.cs` (250-line file) | 250 lines | ~5-15 lines | ~94% |
| Service log (5000 lines) | 5000 lines | ~40+ lines (full stacks kept) | ~99% (noise), stacks intact |

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
| **GitCompact** | `tk git add/commit/push/pull/...` | Keeps first 3 + last 2 lines for verbose operations |
| **Find** | `tk find <path> [flags]` | Count-first find summary with top groups and a few representative paths |
| **View** | `tk view <file[:a-b]>` | Compact file card with symbols/hot ranges, or exact numbered line ranges |
| **Tree** | `tk tree [path]` | Shallow repo tree with directory/file counts |
| **Files** | `tk files [path]` | Compact inventory of key files, optional extension or changed-file filtering |
| **Focus** | `tk focus <word...> [-p path]` | Multi-word OR search ranked by term coverage; quote for an exact phrase; compact summary output |
| **LogFile** | `tk log <file>` | Parses ASP.NET logs: strips startup/framework noise, collapses HTTP request pairs, deduplicates repeated entries (count shown), preserves full stack traces for kept errors |

Unrecognized commands pass through without modification.

## Adding a new filter

1. Create a class implementing `IOutputFilter` in `Filters/`
2. Register it in `FilterRegistry.Resolve()`

```csharp
public sealed class MyFilter : IOutputFilter
{
    public string Apply(string raw, int exitCode)
    {
        // raw = full stdout+stderr, exitCode = process exit code
        // return the filtered string
    }
}
```

## License

MIT
