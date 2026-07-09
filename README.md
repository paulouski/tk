# tk — Token Killer

A CLI proxy between AI coding agents (Claude Code, Codex, opencode, Cursor) and your dev tools: it runs your command, strips the noise from the output, and hands the agent only what it needs to act.

## Why

An agent runs `dotnet build` and gets back 200 lines. 195 of them are NuGet restore messages, MSBuild banners, and duplicated vulnerability warnings. The agent reads all of it, burning tokens and context on noise. `tk` reduces that to a couple of lines with just the outcome, errors, and warnings — same idea for `git status`, `git diff`, `git log`, file reading, repo search, and service log files.

Built by a .NET developer for .NET work, so the deepest filters target the C#/.NET toolchain (builds, restore, semantic refactors). Most of `tk` — git, repo navigation, file reading, search, log triage — is language-agnostic.

## How it works

`tk` wraps a command, runs the real tool, and pipes the output through a filter keyed to that command. If no filter matches, the command passes through unchanged, so `tk <anything>` is always safe.

```
$ dotnet build            # ~180 lines: MSBuild banners, restore noise, per-project output
...

$ tk dotnet build
ok build p=12 t=4.2s
```

On failure the summary line still leads, followed by every distinct error site (no cap, no truncation) and grouped warnings:

```
$ tk dotnet build
FAIL build p=12 e=3 w=5 t=4.2s
---
Errors:
  Program.cs CS0246: The type or namespace name 'Foo' could not be found
  ...
```

Output stays outcome-first: a summary line in stable `key=value` fields (`p`=projects, `e`=errors, `w`=warnings, `t`=time, `pass/fail/skip`=tests, `st/mod/untr`=staged/modified/untracked, `f`=files, `m`=matches, ...), details after. Escalate with `tk --more <command>` for more detail, or `tk --raw <command>` for the original unfiltered output. When lines are dropped, a footer like `hid=42/200 (--more, --raw)` shows how many and how to get them back.

## Install

Requires the [.NET 8 **Runtime**](https://dotnet.microsoft.com/download/dotnet/8) (not the SDK). The install script will offer to install it for you (via `winget` on Windows, Homebrew on macOS) if missing.

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

Both installers also install global instruction blocks to `~/.claude/CLAUDE.md` and `~/.codex/AGENTS.md`. Re-run the same command any time to update to the latest release.

### Build from source (development)

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8).

```powershell
git clone https://github.com/paulouski/tk.git
cd tk
.\build.ps1
```

`build.ps1` compiles and publishes the binary to `%LOCALAPPDATA%\tk`, adds it to user PATH, and runs `tk init`. You can also publish manually:

```bash
dotnet publish Tk.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o <your-bin-dir>
# add <your-bin-dir> to PATH
tk init   # optional: install global Claude + AGENTS instructions
```

## The gist

- `tk dotnet build` / `tk dotnet test` / `tk dotnet restore` — compact build/test/restore summaries
- `tk git status` / `tk git diff` / `tk git log` — count-first, summary-first git output; `tk changes` combines status + diff into one startup card
- `tk focus <word...> [-p path]` — repo-wide search ranked by term coverage, with samples
- `tk view <file>` — file card with symbols and hot ranges instead of dumping the whole file
- `tk refs` / `tk def` / `tk callers` — semantic, LSP-backed navigation (exact references, definitions, call hierarchy)
- `tk mv <old> <new>` / `tk rename <file:line:col> <name>` — move/rename in place, preserving git history, instead of an agent rewriting a whole file

Full command reference, including all flags and the filter table: **[docs/COMMANDS.md](docs/COMMANDS.md)**.

## Modules

`tk` is organized into feature groups so you only enable what you use:

- `core` — always on (navigation, search, view, git, `init`, `module`, …)
- `dotnet` — `tk quality`, `tk dotnet build|test|restore`
- `unity` — `tk unity tree|files|status`
- `lsp` — semantic navigation: `tk refs`, `tk def`, `tk impl`, `tk callers`, `tk calls`, `tk sig`, `tk sym`, `tk diag`, `tk fix`, `tk rename`, `tk lsp status|stop`

`tk module list|enable <name>|disable <name>` toggles a group. Disabling a module removes its commands and stops advertising it to your agents. Run `tk init` to (re)install/update the instruction blocks in `~/.claude/CLAUDE.md`, `~/.codex/AGENTS.md`, and `~/.config/opencode/AGENTS.md` — idempotent, safe to re-run.

## License

MIT
