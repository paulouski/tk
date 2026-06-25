using Tk.Commands;

namespace Tk.Modules;

/// <summary>
/// Single source of truth for all registered modules and their command instances.
/// </summary>
public static class ModuleCatalog
{
    private const string CoreSnippet = """
        ## tk Global Compact Workflow

        `tk` turns noisy command and git output into compact, complete summaries. That — plus
        semantic code navigation — is its real strength. For text search and full file reads,
        native Grep/Read beat `tk`; treat `tk`'s search/read as orientation only.

        Use `tk` for (where it wins):
        - `tk git status|diff|log` — compact, complete git output
        - `tk log <file>` — compacted file history
        - `tk changes` — working-tree change summary
        - `tk mv <old> <new>` — move a file preserving git history (use instead of delete+recreate; keeps git blame and saves tokens). When moving across folders, check the file's namespace and update references afterward — tk does not rewrite them.
        - plus the `dotnet` (build/test/restore) and `lsp` (refs/callers/def/rename) module commands listed below, when enabled — prefer these.

        Orientation only — prefer native Grep/Read for real work:
        - `tk tree`, `tk files` — quick repo shape
        - `tk focus <word...> [-p path]` (`--files-only`) — rough first sweep only. Multiple words = OR sweep ranked by how many distinct words each file matches (term coverage); `-p <path>` to scope; quote `"exact phrase"` for a literal phrase. To find a symbol, its callers, or its definition use `tk refs`/`tk callers`/`tk def` (or native Grep), not focus
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
        """;

    private const string CoreAgentsSnippet = """
        # tk Global Agent Preset

        `tk` gives compact, complete output for noisy commands and git, plus semantic code
        navigation. Those are its strengths — reach for them first. For text search and full
        file reads, native Grep/Read are better; `tk`'s search/read are orientation only.

        Prefer `tk` for (where it wins):
        - `tk git status|diff|log` — compact, complete git output
        - `tk log <file>` — compacted file history
        - `tk changes` — working-tree change summary
        - `tk mv <old> <new>` — move a file preserving git history; NEVER delete and recreate a file to rename/relocate it — use this instead to keep git blame and avoid re-emitting file content. After moving across folders, check the file's namespace and update references — tk does not rewrite them.
        - plus the `dotnet` (build/test/restore) and `lsp` (refs/callers/def/rename) module commands listed below, when enabled — prefer these.

        Orientation only — prefer native Grep/Read for real work:
        - `tk tree`, `tk files` — quick repo shape
        - `tk focus <word...> [-p path]` (`--files-only`) — rough first sweep only. Multiple words = OR sweep ranked by how many distinct words each file matches (term coverage); `-p <path>` to scope; quote `"exact phrase"` for a literal phrase. To find a symbol, its callers, or its definition use `tk refs`/`tk callers`/`tk def` (or native Grep), not focus
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
        """;

    private const string DotnetSnippet = """
        - `tk quality [path]`
        - `tk dotnet build|test|restore`
        """;

    private const string DotnetAgentsSnippet = """
        - `tk quality [path]`
        - `tk dotnet build|test|restore`
        """;

    private const string UnitySnippet = """
        - `tk unity tree|files|status` (Unity projects: hides Library/Temp/Logs/.meta)
        """;

    private const string UnityAgentsSnippet = """
        - `tk unity tree|files|status` (Unity projects: hides Library/Temp/Logs/.meta)
        """;

    private const string LspSnippet = """
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

    public static IReadOnlyList<ModuleDescriptor> All { get; } =
    [
        new ModuleDescriptor(
            Name: "core",
            Commands:
            [
                new LsCommand(),
                new ViewCommand(),
                new InitCommand(),
                new LogCommand(),
                new TreeCommand(),
                new FilesCommand(),
                new ChangesCommand(),
                new BranchCommand(),
                new GitCommand(),
                new FocusCommand(),
                new SwitchCommand(),
                new ModuleCommand(),
                new MvCommand(),
            ],
            AlwaysOn: true,
            InitSnippet: CoreSnippet),

        new ModuleDescriptor(
            Name: "dotnet",
            Commands: [new QualityCommand()],
            AlwaysOn: false,
            InitSnippet: DotnetSnippet),

        new ModuleDescriptor(
            Name: "unity",
            Commands: [new UnityCommand()],
            AlwaysOn: false,
            InitSnippet: UnitySnippet),

        new ModuleDescriptor(
            Name: "lsp",
            Commands: [new LspStatusCommand(), new RefsCommand(), new CallersCommand(), new DefCommand(), new RenameCommand(), new LspDaemonCommand()],
            AlwaysOn: false,
            InitSnippet: LspSnippet),
    ];

    /// <summary>
    /// Returns the AGENTS.md snippet for a module. Returns null if the module has no agents snippet.
    /// The core module has its own agents-flavored snippet; others reuse the same snippet.
    /// </summary>
    public static string? GetAgentsSnippet(ModuleDescriptor module) =>
        module.Name switch
        {
            "core" => CoreAgentsSnippet,
            "dotnet" => DotnetAgentsSnippet,
            "unity" => UnityAgentsSnippet,
            "lsp" => LspSnippet,
            _ => module.InitSnippet
        };
}
