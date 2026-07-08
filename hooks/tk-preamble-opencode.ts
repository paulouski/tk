// tk-preamble-opencode.ts — opencode plugin that appends a tk cheat-sheet to every
// subagent (task) prompt, porting hooks/tk-agent-preamble.sh.
//
// Installs into ~/.config/opencode/plugins/ alongside tk-route-opencode.ts.
// On the `task` tool.execute.before hook, if args.prompt exists and doesn't already
// contain the [tk cheat-sheet] marker, appends the cheat-sheet block. Idempotent and
// degrades to a no-op on any error.

import type { Plugin } from "@opencode-ai/plugin"

const MARKER = "[tk cheat-sheet]"

const BLOCK = `${MARKER} Mandatory tooling in this environment:
- Symbol lookup: \`tk def <Sym>\` / \`tk refs <Sym>\` / \`tk callers <Sym>\` / \`tk impl <Sym>\` — NEVER grep for class/interface/record/method names.
- After editing a .cs file: \`tk diag <file>\` (sub-second compile check) instead of \`dotnet build\`.
- Run noisy commands BARE: \`dotnet build\`, \`dotnet test\`, \`git status\`, \`git log\` — a hook auto-compacts them. Do not pipe them into head/tail/grep; that defeats compaction.
- Compact output is complete for errors/failures. Need more detail: rerun as \`tk --more ...\` or \`tk --raw ...\`.
- Move files with \`tk mv <old> <new>\` (preserves git history, auto-fixes C# namespaces).
- Read big files in slices: pass offset/limit (a hook caps uncapped Reads of files over 500 lines); locate the slice first via \`tk def\`/\`tk view\`.`

export const TkPreamblePlugin: Plugin = async () => {
  return {
    "tool.execute.before": async (input, output) => {
      if (input.tool !== "task") return

      const prompt: string | undefined = output.args.prompt
      if (typeof prompt !== "string" || !prompt) return
      if (prompt.includes(MARKER)) return

      output.args.prompt = `${prompt}\n\n${BLOCK}`
    },
  }
}
