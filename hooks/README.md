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

### Option 1 — Project-scoped (this repo only)

Add to `.claude/settings.local.json` (gitignored):

```json
"hooks": {
  "PreToolUse": [
    {
      "matcher": "Bash",
      "hooks": [
        {
          "type": "command",
          "command": "${CLAUDE_PROJECT_DIR}/hooks/tk-route.sh",
          "timeout": 5
        }
      ]
    }
  ]
}
```

### Option 2 — Global (all repos)

Add the same block to `~/.claude/settings.json`, using the absolute path to the script:

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

## Requirements

- `jq` on PATH
- `tk` on PATH
