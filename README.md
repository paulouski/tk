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

This builds the project, puts the binary in `%LOCALAPPDATA%\tk`, adds it to user PATH, and installs global instruction blocks to:

- `~/.claude/CLAUDE.md`
- `~/.codex/AGENTS.md`

**Manual install** (if you prefer):

```bash
dotnet publish -c Release -o <your-bin-dir>
# add <your-bin-dir> to PATH
tk init   # optional: install global Claude + AGENTS instructions
```

## Usage (current)

Prefix any command with `tk`. If a filter exists, output is compressed. If not, the command passes through unchanged -- so `tk` is always safe to use.

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
tk files                 # compact key-file inventory
tk files --changed       # changed files only
tk files --ext cs        # files filtered by extension
tk focus CommandRunner   # code-first repo search with top files + samples
tk focus CommandRunner tk --files-only  # only likely files, no sample lines
tk focus Refit . --docs # docs/specs/guides only
tk focus Refit . --all  # include docs/logs/other in the ranking
tk focus "RunAsync(" tk  # same, scoped to a path

# File reading
tk view src/Program.cs          # compact file card: line count, symbols, hot ranges
tk view src/Program.cs:40-90    # numbered exact range
tk view src/Program.cs --symbols # symbols only
tk --raw view src/Program.cs    # full file with numbered lines

# Service logs (ASP.NET / Kestrel / MassTransit)
tk log app.log           # strip startup noise, dedup, keep warnings+errors
tk log app.log --errors  # errors/critical only
tk log app.log --last 20 # last 20 entries
tk log app.log --all     # raw output, no filtering

# Anything else -- passthrough
tk cargo build           # no filter exists, runs as-is
tk npm test              # same -- passthrough
```

## Agent-First vNext (planned)

The current filters already save a lot of tokens, but the next 80/20 step is to optimize for how coding agents actually work:

Several parts of this direction are now already present in the current command set (`git diff`, `rg`/`grep`, `view`, `changes`, `tree`, `files`, `focus`). This section remains the design direction and output target for continued tightening.

1. **Show a tiny summary first**
2. **Show only the top few hot spots**
3. **Expand only when explicitly asked**

This means `tk` should prefer a **state card + targeted drill-down** model over "compressed full output".

### Output style

Planned default output is a short, stable, machine-friendly text format:

```text
ok build p=12 t=0.9s
FAIL build p=12 e=3 w=1 t=2.1s
ok test pass=148 skip=2 t=4.3s
FAIL test pass=147 fail=1 t=4.3s
status st=2 mod=5 untr=12
diff f=8 +120 -34
rg m=53 f=7 top=Program.cs(18),Api.cs(12),Repo.cs(9)
```

Goals:

- **Minimal wording**: fewer tokens than prose-heavy summaries
- **Stable fields**: easier for agents to parse mentally and consistently
- **Fast scanning**: outcome first, details second
- **Safe fallback**: if parsing is uncertain, show a short raw tail instead of guessing

### Planned detail levels

Every high-volume command should eventually support three levels:

- `default` - tiny summary, best token savings
- `--more` - summary plus top few relevant details
- `--raw` - original unfiltered output

Examples:

```bash
tk git diff
tk git diff --more
tk git diff --raw
```

### Priority commands

These are the highest-ROI additions and upgrades for agent workflows.

#### 1. `tk git diff` -> summary-first diff

Why first: diffs are one of the biggest token sinks.

Planned default:

- file count
- total `+` / `-`
- top changed files
- hunk headers only
- changed lines only, no context by default

Example:

```text
diff f=6 +84 -19
top=Program.cs(+20 -4),Api.cs(+18 -2),README.md(+9 -1)
@@ Program.cs 42-60
+ added validation for null command args
- removed duplicate stderr merge branch
```

#### 2. `tk rg` / `tk grep` -> summary by file, not by line dump

Why next: agents often search broadly, and raw grep output explodes quickly.

Planned default:

- total matches
- file count
- top files by match count
- 1-3 representative snippets only

Example:

```text
rg m=53 f=7
top=Program.cs(18),Api.cs(12),Repo.cs(9)
samples:
  "ExecuteAsync(command, ct)"
  "command failed before test results were produced"
```

#### 3. `tk view <file>` -> compact file reader

Why next: reading large files is often a bigger token cost than build logs.

Planned behavior:

- compact file preview instead of raw full-file dump
- collapse repeated blank lines
- trim long comment blocks
- show symbols/sections first for large files
- support line ranges

Examples:

```bash
tk view src/Program.cs
tk view src/Program.cs:120-180
tk view src/Program.cs --symbols
```

Possible output:

```text
view Program.cs lines=240
symbols: Main(12), RunAsync(48), EscapeArg(133)
hot:
  48-97 RunAsync
  133-141 EscapeArg
```

#### 4. `tk tree` / `tk files` -> compact repo map

Why: agents often need a repo map before touching code.

Planned behavior:

- 2-3 levels max
- ignore `.git`, `bin`, `obj`, `node_modules`, generated files
- show counts per directory
- optionally show largest or most relevant files

Examples:

```bash
tk tree
tk files
tk files --top 30
```

#### 5. Existing filters to tighten further

Planned tweaks with strong ROI:

- `dotnet build` / `test` / `restore`: keep success output ultra-short
- `git status`: prefer counts first, then a few paths
- `git log`: reduce default history depth
- `find`: return counts + top directories before long flat lists
- `log`: keep error/warn-centric summary and collapse repeated entries more aggressively

### Proposed command set

This is the preliminary "best for agents" surface area:

| Command | Purpose | Default shape |
|--------|---------|---------------|
| `tk dotnet build` | build summary | `ok/FAIL build ...` |
| `tk dotnet test` | test summary | `ok/FAIL test ...` |
| `tk dotnet restore` | restore summary | `ok/FAIL restore ...` |
| `tk git status` | worktree state | counts first, few paths |
| `tk git diff` | change summary | stat + top hunks |
| `tk git log` | recent history | short capped list |
| `tk rg` / `tk grep` | search summary | matches/files/top files |
| `tk find` | path discovery | count + top results |
| `tk view <file>` | compact file reading | symbols + targeted lines |
| `tk tree` / `tk files` | repo map | shallow structure |
| `tk log <file>` | log triage | compact warn/error summary |

### Design rules

Planned rules for all filters:

- Prefer counts over prose
- Prefer top-N examples over exhaustive lists
- Never hide failures behind optimistic summaries
- If unsure, show a short raw tail
- Default output should fit in a few lines
- Expansion should be explicit, not automatic

### What this optimizes for

This roadmap is optimized for:

- less context waste
- faster agent iteration loops
- fewer follow-up "show me more" retries
- safer debugging when parsers miss a case

Implementation status: **planned / not yet implemented unless already described in the current Usage and Filters sections above**.

## Configuring with Claude Code

```bash
tk init
```

This installs or updates global tk instruction blocks in:

- `~/.claude/CLAUDE.md`
- `~/.codex/AGENTS.md`

The installer is marker-based and idempotent: rerunning `tk init` updates the tk block instead of duplicating it.

## Token savings

| Command | Typical output | After tk | Savings |
|---------|---------------|----------|---------|
| `dotnet build` (success, 12 projects) | ~180 lines | 1 line | ~99% |
| `dotnet build` (3 errors, 5 warnings) | ~180 lines | 8 lines | ~95% |
| `dotnet test` (all pass) | ~60 lines | 1 line | ~98% |
| `git status` (15 files) | ~30 lines | ~12 lines | ~60% |
| `git diff` (500-line diff) | 500 lines | ~10-20 lines | ~95% |
| `changes` (status + diff startup check) | ~530 lines | ~3-12 lines | ~98% |
| `tree` (medium repo) | ~100-500 lines | ~5-15 lines | ~90-98% |
| `focus` (broad search) | ~50-500 lines | ~3-10 lines | ~85-98% |
| `git log` (full format) | ~200 lines | ~30 lines | ~85% |
| `view Program.cs` (250-line file) | 250 lines | ~5-15 lines | ~94% |
| Service log (5000 lines) | 5000 lines | ~40 lines | ~99% |

## Filters

| Filter | Trigger | What it does |
|--------|---------|-------------|
| **DotnetBuild** | `tk dotnet build` | Groups errors/warnings by code, deduplicates NuGet vulnerability warnings, shows project count and duration |
| **DotnetTest** | `tk dotnet test` | Shows pass/fail/skip counts; on failure, lists failed test names with first 5 lines of detail |
| **DotnetRestore** | `tk dotnet restore` | Counts restored projects, surfaces errors, collapses NuGet noise |
| **GitStatus** | `tk git status` | Count-first status summary with staged/modified/untracked counts, branch, and top paths |
| **GitLog** | `tk git log` | Converts full-format log to one-line-per-commit, caps at 30 |
| **GitDiff** | `tk git diff`, `tk git show` | Summary-first diff: file stats, top changed files, compact hunk preview with changed lines only |
| **Changes** | `tk changes` | Compact repo state card combining `git status` and `git diff` |
| **GitCompact** | `tk git add/commit/push/pull/...` | Keeps first 3 + last 2 lines for verbose operations |
| **Find** | `tk find <path> [flags]` | Count-first find summary with top groups and a few representative paths |
| **View** | `tk view <file[:a-b]>` | Compact file card with symbols/hot ranges, or exact numbered line ranges |
| **Tree** | `tk tree [path]` | Shallow repo tree with directory/file counts |
| **Files** | `tk files [path]` | Compact inventory of key files, optional extension or changed-file filtering |
| **Focus** | `tk focus <query> [path]` | Narrow repo search using compact summary output |
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
