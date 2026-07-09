// tk-route-opencode.ts — opencode plugin that ports hooks/tk-route.sh routing rules.
//
// Installs into ~/.config/opencode/plugins/ (global) or .opencode/plugins/ (project).
// Two behaviors:
//   1. bash tool.execute.before: rewrites `dotnet build|test|restore` and
//      `git status|log` (single or in compound `&&`/`||`/`;`/newline chains) to
//      `tk ...`. `git diff|show` is deliberately NOT routed (diffs must stay raw).
//      Heredocs, $(), backticks, and shell control keywords (if/for/while/...) are
//      left untouched — conservative passthrough. A single (non-compound) command
//      that isn't recognized by the tk rules above is offered to `rtk rewrite` as a
//      fallback (if `rtk` is on PATH); compound/piped commands never reach the
//      fallback — only the built-in tokenizer rewrites those segments.
//   2. read tool.execute.before: caps uncapped Reads of files >500 lines at 500.
//
// opencode's tool.execute.before can mutate args but has no additionalContext channel,
// so the symbol-grep nudge and large-read nudge from tk-route.sh are not ported.

import type { Plugin } from "@opencode-ai/plugin"

const READ_CAP_LINES = 500

// --- bash routing ----------------------------------------------------------

const GIT_GLOBAL_FLAGS_RE = /(?:-C|-c)\s+\S+\s+/
const ENV_PREFIX_RE = /^(?:[A-Za-z_][A-Za-z0-9_]*=\S*\s+)+/
const CONTROL_KEYWORD_RE = /(?:^|[;&|\n])\s*(?:if|then|else|elif|fi|for|while|until|do|done|case|esac|function)(?:\s|[;&|]|$)/

function stripShellLiterals(s: string): string {
  return s
    .replace(/\\./g, "")
    .replace(/'[^']*'/g, "")
    .replace(/"[^"]*"/g, "")
}

function matchTkRoute(rest: string): boolean {
  if (/^dotnet\s+(build|test|restore)(\s|$)/.test(rest)) return true
  const probe = stripShellLiterals(rest)
  return new RegExp(`^git\\s+(?:${GIT_GLOBAL_FLAGS_RE.source})*(status|log)(\\s|$)`).test(probe)
}

function rewriteSegment(segment: string): string | null {
  const leadingMatch = segment.match(/^\s*/)
  const trailingMatch = segment.match(/\s*$/)
  const leading = leadingMatch ? leadingMatch[0] : ""
  const trailing = trailingMatch ? trailingMatch[0] : ""
  const core = segment.slice(leading.length, segment.length - trailing.length)
  if (!core) return null

  // Strip leading VAR=val env prefixes.
  let envPrefix = ""
  let rest = core
  const envMatch = rest.match(ENV_PREFIX_RE)
  if (envMatch) {
    envPrefix = envMatch[0]
    rest = core.slice(envPrefix.length)
  }

  // Never double-wrap tk or rtk.
  if (rest === "tk" || rest.startsWith("tk ")) return null
  if (rest === "rtk" || rest.startsWith("rtk ")) return null

  if (matchTkRoute(rest)) {
    return `${leading}${envPrefix}tk ${rest}${trailing}`
  }

  // git diff/show deliberately excluded.
  const routeProbe = stripShellLiterals(rest)
  if (new RegExp(`^git\\s+(?:${GIT_GLOBAL_FLAGS_RE.source})*(diff|show)(\\s|$)`).test(routeProbe)) {
    return null
  }

  return null
}

function tokenizeCompound(input: string): { segs: string[]; seps: string[]; unterminated: boolean } {
  const segs: string[] = []
  const seps: string[] = []
  let current = ""
  let quote = ""
  let escaped = false

  for (let i = 0; i < input.length; i++) {
    const ch = input[i]

    if (escaped) {
      current += ch
      escaped = false
      continue
    }
    if (ch === "\\" && quote !== "'") {
      current += ch
      escaped = true
      continue
    }
    if (quote) {
      if (ch === quote) quote = ""
      current += ch
      continue
    }
    if (ch === "'" || ch === '"') {
      quote = ch
      current += ch
      continue
    }

    const next = input[i + 1] ?? ""
    if ((ch === "&" && next === "&") || (ch === "|" && next === "|")) {
      segs.push(current)
      seps.push(ch + next)
      current = ""
      i++
      continue
    }
    if (ch === ";" || ch === "\n") {
      segs.push(current)
      seps.push(ch)
      current = ""
      continue
    }
    if (ch === "|") {
      segs.push(current)
      seps.push("|")
      current = ""
      continue
    }

    current += ch
  }

  return { segs: [...segs, current], seps: [...seps, ""], unterminated: quote !== "" }
}

function rewriteCompoundCommand(input: string): string | null {
  const { segs, seps, unterminated } = tokenizeCompound(input)
  if (unterminated) return null

  let result = ""
  let changed = false
  // Build pipeline groups: consecutive segments joined by "|".
  let group: string[] = []
  for (let idx = 0; idx < segs.length; idx++) {
    group.push(segs[idx])
    const sep = seps[idx]
    if (sep === "|") continue // still building the same pipeline group

    // Simple rewrite: try each segment in the group individually (no three-tier
    // guard — conservative: if any segment in a pipeline is a content filter,
    // we don't rewrite the LHS). For single-segment groups this is exact.
    if (group.length === 1) {
      const rewritten = rewriteSegment(group[0])
      if (rewritten !== null) {
        result += rewritten + sep
        changed = true
      } else {
        result += group[0] + sep
      }
    } else {
      // Multi-segment pipeline: only rewrite if LHS matches AND all downstream
      // are display modifiers (head/tail/cat). Otherwise passthrough.
      const lhsRewritten = rewriteSegment(group[0])
      const allDisplay = group.slice(1).every((seg) => {
        const core = seg.trim()
        return /^(head|tail|cat)(\s|$)/.test(core)
      })
      if (lhsRewritten !== null && allDisplay) {
        result += lhsRewritten + "|" + group.slice(1).join("|") + sep
        changed = true
      } else {
        result += group.join("|") + sep
      }
    }
    group = []
  }

  return changed ? result : null
}

async function rewriteBashCommand(
  cmd: string,
  $: Parameters<Plugin>[0]["$"],
): Promise<string | null> {
  const stripped = stripShellLiterals(cmd)

  // Hard-unsafe: leave untouched.
  if (/['"]/.test(stripped)) return null // unbalanced quote survived stripping
  if (/`/.test(stripped)) return null // backtick command substitution
  if (/\$\(/.test(stripped)) return null // $() command substitution
  if (/<<</.test(stripped)) return null // heredoc / herestring
  if (/&\s*$/.test(stripped)) return null // trailing background &
  if (/\\\n/.test(cmd)) return null // backslash-newline continuation
  if (CONTROL_KEYWORD_RE.test(stripped)) return null // if/for/while/... shell script

  // Compound?
  const isCompound = /&&|\|\||;|\||\n/.test(stripped)
  if (isCompound) {
    return rewriteCompoundCommand(cmd)
  }

  const rewritten = rewriteSegment(cmd)
  if (rewritten !== null) return rewritten

  return rtkFallback(cmd, $)
}

// --- rtk fallback ------------------------------------------------------

let rtkAvailableCache: boolean | null = null

async function isRtkAvailable($: Parameters<Plugin>[0]["$"]): Promise<boolean> {
  if (rtkAvailableCache !== null) return rtkAvailableCache
  const out = await $`command -v rtk`.quiet().nothrow()
  rtkAvailableCache = out.exitCode === 0
  return rtkAvailableCache
}

// Single (non-compound) commands only — compound/piped commands are handled
// exclusively by the quote-aware tokenizer above and never reach this fallback.
async function rtkFallback(
  cmd: string,
  $: Parameters<Plugin>[0]["$"],
): Promise<string | null> {
  const trimmed = cmd.trim()
  if (!trimmed) return null

  let envPrefix = ""
  let rest = trimmed
  const envMatch = rest.match(ENV_PREFIX_RE)
  if (envMatch) {
    envPrefix = envMatch[0]
    rest = trimmed.slice(envPrefix.length)
  }
  if (!rest) return null

  // Never double-wrap tk or rtk.
  if (rest === "tk" || rest.startsWith("tk ")) return null
  if (rest === "rtk" || rest.startsWith("rtk ")) return null

  if (!(await isRtkAvailable($))) return null
  const out = await $`rtk rewrite ${rest}`.quiet().nothrow()
  if (out.exitCode !== 0) return null
  const rewrittenRest = out.text().trim()
  if (!rewrittenRest) return null
  return `${envPrefix}${rewrittenRest}`
}

// --- read cap --------------------------------------------------------------

async function capRead(filePath: string, offset: unknown, limit: unknown): Promise<{ limit: number } | null> {
  if (offset != null || limit != null) return null
  const fs = await import("fs/promises")
  let lines: number
  try {
    const content = await fs.readFile(filePath, "utf8")
    lines = content.split("\n").length - (content.endsWith("\n") ? 1 : 0)
  } catch {
    return null
  }
  if (lines <= READ_CAP_LINES) return null
  return { limit: READ_CAP_LINES }
}

// --- plugin ---------------------------------------------------------------

export const TkRoutePlugin: Plugin = async ({ $ }) => {
  return {
    "tool.execute.before": async (input, output) => {
      if (input.tool === "bash") {
        const cmd: string = output.args.command
        if (typeof cmd !== "string" || !cmd) return
        const rewritten = await rewriteBashCommand(cmd, $)
        if (rewritten !== null && rewritten !== cmd) {
          output.args.command = rewritten
        }
        return
      }

      if (input.tool === "read") {
        const filePath: string = output.args.filePath
        if (typeof filePath !== "string" || !filePath) return
        const cap = await capRead(filePath, output.args.offset, output.args.limit)
        if (cap) {
          output.args.limit = cap.limit
        }
        return
      }
    },
  }
}
