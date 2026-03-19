# tk -- Token Killer

CLI proxy that sits between AI coding assistants (Claude Code, Copilot, Cursor) and your dev tools. It intercepts command output and strips the noise -- build logs, git verbosity, NuGet warnings, startup spam -- so the AI sees only what matters.

Result: **60-99% fewer tokens** per command, which means faster responses, cheaper API calls, and more room in the context window for actual work.

## Why

AI assistants run `dotnet build` and get back 200 lines. 195 of them are NuGet restore messages, MSBuild banners, and duplicated vulnerability warnings. The assistant reads all of it, burning tokens on noise. `tk` reduces that to 2-3 lines with just the outcome, errors, and warnings.

Same idea for `git status`, `git diff`, `git log`, and service log files.

## Install

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8).

```powershell
git clone https://github.com/<you>/tk.git
cd tk
.\install.ps1
```

This builds the project, puts the binary in `%LOCALAPPDATA%\tk`, adds it to user PATH, and installs Claude Code instructions to `~/.claude/CLAUDE.md`.

**Manual install** (if you prefer):

```bash
dotnet publish -c Release -o <your-bin-dir>
# add <your-bin-dir> to PATH
tk init   # optional: install Claude Code instructions
```

## Usage

Prefix any command with `tk`. If a filter exists, output is compressed. If not, the command passes through unchanged -- so `tk` is always safe to use.

```bash
# .NET
tk dotnet build          # errors + warnings grouped by code, NuGet vulns summarized
tk dotnet test           # pass/fail summary, failed test details only
tk dotnet restore        # project count + errors, skip the noise

# Git
tk git status            # grouped by staged/modified/untracked, truncated
tk git log               # one-line-per-commit format, max 30
tk git diff              # stat summary + truncated hunks (max 200 lines)
tk git show              # same as diff
tk git push              # ultra-compact (keep first 3 + last 2 lines)
tk git commit            # ultra-compact
# ... any other git subcommand gets compact treatment or passthrough

# Service logs (ASP.NET / Kestrel / MassTransit)
tk log app.log           # strip startup noise, dedup, keep warnings+errors
tk log app.log --errors  # errors/critical only
tk log app.log --last 20 # last 20 entries
tk log app.log --all     # raw output, no filtering

# Anything else -- passthrough
tk cargo build           # no filter exists, runs as-is
tk npm test              # same -- passthrough
```

## Configuring with Claude Code

```bash
tk init
```

This appends tk instructions to `~/.claude/CLAUDE.md`. Claude Code will use `tk` automatically for all matching commands. Idempotent -- safe to run multiple times.

## Token savings

| Command | Typical output | After tk | Savings |
|---------|---------------|----------|---------|
| `dotnet build` (success, 12 projects) | ~180 lines | 1 line | ~99% |
| `dotnet build` (3 errors, 5 warnings) | ~180 lines | 8 lines | ~95% |
| `dotnet test` (all pass) | ~60 lines | 1 line | ~98% |
| `git status` (15 files) | ~30 lines | ~12 lines | ~60% |
| `git diff` (500-line diff) | 500 lines | ~50 lines | ~90% |
| `git log` (full format) | ~200 lines | ~30 lines | ~85% |
| Service log (5000 lines) | 5000 lines | ~40 lines | ~99% |

## Filters

| Filter | Trigger | What it does |
|--------|---------|-------------|
| **DotnetBuild** | `tk dotnet build` | Groups errors/warnings by code, deduplicates NuGet vulnerability warnings, shows project count and duration |
| **DotnetTest** | `tk dotnet test` | Shows pass/fail/skip counts; on failure, lists failed test names with first 5 lines of detail |
| **DotnetRestore** | `tk dotnet restore` | Counts restored projects, surfaces errors, collapses NuGet noise |
| **GitStatus** | `tk git status` | Groups files into Staged/Modified/Untracked sections with counts, truncates long lists |
| **GitLog** | `tk git log` | Converts full-format log to one-line-per-commit, caps at 30 |
| **GitDiff** | `tk git diff`, `tk git show` | File stat summary + truncated hunks, max 200 content lines across all files |
| **GitCompact** | `tk git add/commit/push/pull/...` | Keeps first 3 + last 2 lines for verbose operations |
| **LogFile** | `tk log <file>` | Parses ASP.NET logs: strips startup/framework noise, collapses HTTP request pairs, deduplicates, trims stack traces to 3 frames |

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
