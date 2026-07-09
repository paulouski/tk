// tk-slim-opencode.ts — opencode plugin that trims token overhead unrelated to
// bash/git routing (see tk-route-opencode.ts for that).
//
// Installs into ~/.config/opencode/plugins/ (global) or .opencode/plugins/ (project).
// Two behaviors:
//   1. tool.definition: shortens the built-in tool descriptions sent to the model
//      (read/edit/apply_patch/write/bash/glob/grep/list/fetch) to a one-line summary,
//      trimming prompt overhead from opencode's default (verbose) descriptions.
//   2. tool.execute.after (read only): compacts the `read` tool's XML-ish output —
//      makes the `<path>` relative to the project directory, drops the redundant
//      `<type>file</type>` tag, and strips the "(End of file - total N lines)" footer.
//      Directory reads (`<type>directory</type>`) are left untouched.

import type { Plugin } from "@opencode-ai/plugin"
import * as path from "path"

const SLIM: Record<string, string> = {
  read: "Read file content.",
  edit: "Edit file.",
  apply_patch: "Apply a patch to files.",
  write: "Write file.",
  bash: "Run shell command.",
  glob: "Find files.",
  grep: "Search in files.",
  list: "List directory.",
  fetch: "Fetch URL.",
}

export const TkSlimPlugin: Plugin = async ({ directory }) => {
  return {
    "tool.definition": async (input, output) => {
      if (SLIM[input.toolID]) output.description = SLIM[input.toolID]
    },
    "tool.execute.after": async (input, output) => {
      if (input.tool !== "read") return
      if (output.output.includes("<type>directory</type>")) return
      const pathMatch = output.output.match(/<path>(.+?)<\/path>/)
      if (!pathMatch) return
      const absPath = path.normalize(pathMatch[1])
      const relPath = path.relative(directory, absPath)
      output.output = output.output.replace(`<path>${pathMatch[1]}</path>`, `<path>${relPath}</path>`)
      output.output = output.output.replace("<type>file</type>\n", "")
      output.output = output.output.replace(/\n\n\(End of file - total \d+ lines\)\n/, "\n")
    },
  }
}
