namespace Tk.Modules;

/// <summary>
/// Markdown instruction snippets emitted by <c>tk init</c> into CLAUDE.md / AGENTS.md / the
/// opencode global rules file. Kept separate from <see cref="ModuleCatalog"/> so the catalog
/// file stays focused on command rows. See InitCommand.
/// </summary>
internal static class ModuleSnippets
{
    internal const string CoreSnippet = """
        ## tk Global Compact Workflow

        `tk` turns noisy command and git output into compact, complete summaries, and (with the
        lsp module) is how you navigate code by symbol. Those are its strengths. For free-text
        search and reading file contents, native Grep/Read are better — treat `tk`'s text-search
        and `tk view`/read as orientation only. One hard exception, below: symbol lookup is always
        `tk`, never grep.

        Use `tk` for (where it wins):
        - Symbol lookup (lsp module): `tk def <symbol>` / `tk refs <symbol>` / `tk callers <symbol>` — where a symbol is defined, all references, who calls it. When lsp is enabled this is the ONLY correct way to locate a symbol — NEVER `grep`/`rg` for `class X` / `record X` / `interface X` / a method name. `tk rename <file:line:col> <new>` refactors repo-wide.
        - `tk git status|diff|log` — compact, complete git output
        - `tk log <file>` — compacted file history
        - `tk changes` — working-tree change summary
        - `tk mv <old> <new>` — move a file preserving git history (use instead of delete+recreate; keeps git blame and saves tokens). For a .cs file moved across folders, tk fixes the namespace to match the new path and patches referencing files automatically (needs the lsp module); if it can't (module off, unconventional namespace), it still moves the file and prints why the namespace wasn't touched. `--no-fix-ns` skips this; `--ns <namespace>` sets the target explicitly.

        Orientation only — prefer native Grep/Read for real work:
        - `tk tree`, `tk files` — quick repo shape
        - `tk focus <word...> [-p path]` (`--files-only`) — rough first sweep only. Multiple words = OR sweep ranked by how many distinct words each file matches (term coverage); `-p <path>` to scope; quote `"exact phrase"` for a literal phrase. To find a symbol, its callers, or its definition use `tk def`/`tk refs`/`tk callers`, not focus.
        - `tk view <file>` — large-file map (symbols/hot TOC) only; to read a file, use native Read

        Escalate detail as:
        - default `tk ...`
        - `tk --more ...`
        - `tk --raw ...`

        Compact keys:
        - `p=projects`, `t=time`, `e=errors`, `w=warnings`, `i=info`
        - `nu=NuGet vulnerabilities`, `pass=passed`, `fail=failed`, `skip=skipped`
        - `st=staged`, `mod=modified`, `untr=untracked`, `f=files`, `d=directories`, `m=matches`
        - `n=count`, `top=top items`, `br=branch`, `up=up to date`, `file=file`
        - `hid=hidden/total lines`

        If compact output is enough to choose the next step, do not rerun raw.
        If compact output is ambiguous, try `--more`.
        If exact original output is needed, use `--raw`.

        Routing: a PreToolUse hook (hooks/tk-route.sh) auto-rewrites bare `dotnet build|test|restore`
        and `git status|log` to their `tk` equivalents, and falls back to `rtk rewrite` for other
        recognized generic commands (`git diff`/`show` deliberately stay raw — tk compaction can
        drop changed lines). You don't need to type `tk` yourself for those. But for tk's unique
        commands — `view`, `focus`, `mv`, `log <service-log>`, `unity`, and the lsp module
        (`def`/`refs`/`callers`/`rename`) — always call `tk` explicitly; rtk does not have them.

        Run noisy commands BARE — never defensively pipe them. `dotnet build 2>&1 | tail -50`,
        `git status | head -20`, `dotnet test | grep FAIL` and similar defeat the whole setup:
        the pipe makes the command compound, the router skips it, and you get raw output instead
        of a compact summary that already keeps every error/failure. Trust the compaction: bare
        `dotnet build`, bare `git status` — escalate with `--more`/`--raw` only if needed.
        """;

    internal const string CoreAgentsSnippet = """
        # tk Global Agent Preset

        `tk` gives compact, complete output for noisy commands and git, and (with the lsp module)
        navigates code by symbol. Those are its strengths — reach for them first. For free-text
        search and reading file contents, native Grep/Read are better; treat `tk`'s text-search and
        `tk view`/read as orientation only. One hard exception: symbol lookup is always `tk`, never grep.

        Prefer `tk` for (where it wins):
        - Symbol lookup (lsp module): `tk def <symbol>` / `tk refs <symbol>` / `tk callers <symbol>` — definition, all references, callers. When lsp is enabled this is the ONLY correct way to locate a symbol; NEVER `grep`/`rg` for `class X` / `record X` / `interface X` / a method name. `tk rename` refactors repo-wide.
        - `tk git status|diff|log` — compact, complete git output
        - `tk log <file>` — compacted file history
        - `tk changes` — working-tree change summary
        - `tk mv <old> <new>` — move a file preserving git history; NEVER delete and recreate a file to rename/relocate it — use this instead to keep git blame and avoid re-emitting file content. For a .cs file moved across folders, tk fixes the namespace to match the new path and patches referencing files automatically (needs the lsp module); if it can't (module off, unconventional namespace), it still moves the file and prints why the namespace wasn't touched. `--no-fix-ns` skips this; `--ns <namespace>` sets the target explicitly.

        Orientation only — prefer native Grep/Read for real work:
        - `tk tree`, `tk files` — quick repo shape
        - `tk focus <word...> [-p path]` (`--files-only`) — rough first sweep only. Multiple words = OR sweep ranked by how many distinct words each file matches (term coverage); `-p <path>` to scope; quote `"exact phrase"` for a literal phrase. To find a symbol, its callers, or its definition use `tk def`/`tk refs`/`tk callers`, not focus.
        - `tk view <file>` — large-file map (symbols/hot TOC) only; to read a file, use native Read

        Escalation order:
        1. `tk ...`
        2. `tk --more ...`
        3. `tk --raw ...`

        Stable compact keys:
        - `p`, `t`, `e`, `w`, `i`, `nu`
        - `pass`, `fail`, `skip`
        - `st`, `mod`, `untr`
        - `f`, `d`, `m`, `n`
        - `top`, `br`, `up`, `file`
        - `hid` (hidden/total lines when output was filtered)

        If compact output is sufficient to choose the next action, do not rerun the raw command.
        If `tk` emits a raw tail fallback, treat it as parser uncertainty and inspect more carefully.

        No PreToolUse hook runs here — call the proxies explicitly. Use `tk` for `git status`/`log`,
        `dotnet build|test|restore`, and tk's unique commands (`view`, `focus`, `mv`,
        `log <service-log>`, `unity`, and the lsp module) that `rtk` does not have. Use `rtk`
        explicitly for other generic command compaction it supports when tk doesn't cover it.

        Run noisy commands through the proxy BARE — never defensively pipe them. `tk dotnet build`
        instead of `dotnet build 2>&1 | tail -50`; `tk git status` instead of `git status | head`.
        The compaction already keeps every error and failed test; piping around it only loses the
        summary. Escalate with `tk --more` / `tk --raw` when you truly need more.

        - `tk quality [path]`
        - `tk dotnet build|test|restore`

        Symbol lookup is ALWAYS these, NEVER `grep`/`rg` for `class X` / `record X` / `interface X` or a method name:
        - `tk refs <symbol>` — all references to a symbol by name (resolves name → position; lists candidates if ambiguous)
        - `tk refs <file:line:col>` — find references at position
        - `tk callers <symbol>` — who calls this symbol (incoming call hierarchy; crosses interface dispatch)
        - `tk callers <file:line:col>` — incoming callers at position
        - `tk def <symbol>` — show where a symbol is defined (jumps to source; lists candidates if ambiguous)
        - `tk def <file:line:col>` — go to definition at position
        - `tk rename <file:line:col> <newName>` — rename a symbol everywhere (for refactoring existing code, NOT for atomic single-file micro-edits)
        - `tk lsp status` — check LSP daemon status
        - `tk lsp stop` — stop LSP daemon
        """;

    internal const string DotnetSnippet = """
        - `tk quality [path]`
        - `tk dotnet build|test|restore`
        """;

    internal const string DotnetAgentsSnippet = """
        - `tk quality [path]`
        - `tk dotnet build|test|restore`
        """;

    internal const string UnitySnippet = """
        - `tk unity tree|files|status` (Unity projects: hides Library/Temp/Logs/.meta)
        """;

    internal const string UnityAgentsSnippet = """
        - `tk unity tree|files|status` (Unity projects: hides Library/Temp/Logs/.meta)
        """;

    internal const string LspSnippet = """
        Symbol lookup is ALWAYS these, NEVER `grep`/`rg` for `class X` / `record X` / `interface X` or a method name:
        - `tk diag <file|project|dir>` — compile diagnostics straight from the warm daemon, no build. To check for compile errors after an edit, prefer this over `dotnet build` — it's sub-second; `dotnet build` is still needed to produce binaries or run tests.
        - `tk diag --changed` — diagnostics for every changed .cs file (staged+modified+untracked), no path needed
        - `tk refs <symbol>` — all references to a symbol by name (resolves name → position; lists candidates if ambiguous)
        - `tk refs <file:line:col>` — find references at position
        - `tk callers <symbol>` — who calls this symbol (incoming call hierarchy; crosses interface dispatch)
        - `tk callers <file:line:col>` — incoming callers at position
        - `tk calls <symbol>` — outgoing calls made by this symbol (call hierarchy, opposite direction of `tk callers`); some Roslyn LS builds don't implement this direction and answer empty — treat n=0 as inconclusive, not proof of no calls
        - `tk def <symbol>` — show where a symbol is defined (jumps to source; lists candidates if ambiguous)
        - `tk def <file:line:col>` — go to definition at position
        - `tk impl <symbol>` — who implements this interface / overrides this abstract member (textDocument/implementation)
        - `tk impl <file:line:col>` — find implementations at position
        - `tk sig <symbol|file:line:col>` — signature + doc-comment summary for a symbol (hover)
        - `tk sym <query>` — fuzzy workspace-wide symbol search, grouped by file
        - `tk fix <file>` — apply only safe quick-fixes: add a missing using / remove an unnecessary using; reports "unsupported by server" rather than applying anything wider
        - `tk rename <file:line:col> <newName>` — rename a symbol everywhere (for refactoring existing code, NOT for atomic single-file micro-edits)
        - `tk lsp status` — check LSP daemon status
        - `tk lsp stop` — stop LSP daemon
        """;

    /// <summary>
    /// Returns the AGENTS.md snippet for a module. Returns null if the module has no agents snippet.
    /// The core module has its own agents-flavored snippet; others reuse the same snippet.
    /// </summary>
    internal static string? GetAgentsSnippet(ModuleDescriptor module) =>
        module.Name switch
        {
            "core" => CoreAgentsSnippet,
            "dotnet" => DotnetAgentsSnippet,
            "unity" => UnityAgentsSnippet,
            "lsp" => LspSnippet,
            _ => module.InitSnippet
        };
}
