#!/usr/bin/env bash
# tk-agent-preamble.sh — PreToolUse hook for the Task tool: appends a tk cheat-sheet
# to every subagent prompt, so freshly spawned subagents get the same tooling
# guidance the main agent has in its global CLAUDE.md, without relying on the main
# agent to copy it into every delegated prompt by hand.
#
# Idempotent: if the prompt already contains the "[tk cheat-sheet]" marker (e.g. the
# main agent already pasted it, or this hook already ran once on this prompt), the
# prompt is left untouched.
#
# Design note: this hook degrades to a no-op — if the running Claude Code version
# ignores `updatedInput`, the original prompt simply runs unchanged; nothing breaks.

INPUT=$(cat)

tool=$(printf '%s' "$INPUT" | jq -r '.tool_name // empty' 2>/dev/null)
[[ "$tool" != "Task" ]] && exit 0

prompt=$(printf '%s' "$INPUT" | jq -r '.tool_input.prompt // empty' 2>/dev/null)
[[ -z "$prompt" ]] && exit 0

MARKER='[tk cheat-sheet]'
[[ "$prompt" == *"$MARKER"* ]] && exit 0

BLOCK='[tk cheat-sheet] Mandatory tooling in this environment:
- Symbol lookup: `tk def <Sym>` / `tk refs <Sym>` / `tk callers <Sym>` / `tk impl <Sym>` — NEVER grep for class/interface/record/method names.
- After editing a .cs file: `tk diag <file>` (sub-second compile check) instead of `dotnet build`.
- Run noisy commands BARE: `dotnet build`, `dotnet test`, `git status`, `git log` — a hook auto-compacts them. Do not pipe them into head/tail/grep; that defeats compaction.
- Compact output is complete for errors/failures. Need more detail: rerun as `tk --more ...` or `tk --raw ...`.
- Move files with `tk mv <old> <new>` (preserves git history, auto-fixes C# namespaces).
- Read big files in slices: pass offset/limit (a hook caps uncapped Reads of files over 500 lines); locate the slice first via `tk def`/`tk view`.'

new_prompt="${prompt}"$'\n\n'"${BLOCK}"

jq -n --arg p "$new_prompt" \
  '{hookSpecificOutput:{hookEventName:"PreToolUse",permissionDecision:"allow",updatedInput:{prompt:$p}}}'
