# tk-route hook

A Claude Code `PreToolUse` hook that routes single-command Bash calls through `tk`
for compact output, and nudges grep-for-symbol toward `tk` semantic navigation.

## What it does

- **Rewrites** single `dotnet build|test|restore` and `git status|log`
  commands to their `tk` equivalents before execution, so Claude gets compact,
  token-efficient output instead of raw verbose output. `git diff`/`git show` are
  intentionally NOT routed — the agent often needs the full patch, and tk compaction
  can drop added/changed lines and mislead it.
- **Nudges** grep commands that look like symbol searches (e.g. `grep -rn "class Foo" src`)
  with a hint to use `tk def|refs|callers <Symbol>` instead, which resolves cross-file
  and crosses interface dispatch.

## Safety

- **Skips compound commands**: any command containing `|`, `&&`, a trailing `&`, `;`,
  `` ` ``, `$(`, or a newline is left completely untouched. Redirects (`>`, `<`, `2>&1`,
  `&>file`) are safe and do not block rewriting.
- **Never double-wraps**: if the command already starts with `tk `, it is not touched.
- **Degrades to a no-op**: if the running Claude Code version ignores `updatedInput` or
  `additionalContext` in hook output, the original command runs unchanged. Nothing breaks.

## How to activate

`tk` is a global tool (its guidance lives in `~/.claude/CLAUDE.md`), so the hook
that enforces that guidance belongs in your **global** settings too. Install it
once, in one place — do not enable both, or it fires twice per command.

### Recommended — Global (all repos)

Add to `~/.claude/settings.json`, using the absolute path to the script:

```json
"hooks": {
  "PreToolUse": [
    {
      "matcher": "Bash",
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

Every time the hook takes an action it appends one tab-separated line to a single
global file, `~/.claude/tk-hook.log` (one file, not per-repo, so a globally
installed hook never litters working trees). Each line carries the `cwd` column to
attribute the action to a repo:

```
<epoch_seconds>\t<decision>\t<cwd>\t<command>
```

`<decision>` is one of:

- `route` — command was rewritten (e.g. `dotnet build` → `tk dotnet build`)
- `nudge` — symbol-grep was detected and the hint was emitted

Skip-throughs (no match, compound commands, etc.) are not logged; only the two
active cases above are recorded. The log is append-only and safe to delete at any
time — the hook will recreate it. If `$HOME` is unavailable or the path is not
writable, logging is silently skipped; the hook still completes normally.

## Requirements

- `jq` on PATH
- `tk` on PATH
