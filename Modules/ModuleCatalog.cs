using Tk.Commands;

namespace Tk.Modules;

/// <summary>
/// Single source of truth for all registered modules and their command instances.
/// </summary>
public static class ModuleCatalog
{
    private const string CoreSnippet = """
        ## tk Global Compact Workflow

        Prefer `tk` for noisy commands and repo exploration when exact raw output is not required.

        Use:
        - `tk changes`
        - `tk tree`
        - `tk files`
        - `tk focus <query> [path]`
        - `tk focus <query> [path] --files-only`
        - `tk view <file>`
        - `tk git status|diff|log`
        - `tk log <file>`

        Escalate detail as:
        - default `tk ...`
        - `tk --more ...`
        - `tk --raw ...`

        Compact keys:
        - `p=projects`, `t=time`, `e=errors`, `w=warnings`, `i=info`
        - `nu=NuGet vulnerabilities`, `pass=passed`, `fail=failed`, `skip=skipped`
        - `st=staged`, `mod=modified`, `untr=untracked`, `f=files`, `d=directories`, `m=matches`
        - `n=count`, `top=top items`, `br=branch`, `up=up to date`, `file=file`

        If compact output is enough to choose the next step, do not rerun raw.
        If compact output is ambiguous, try `--more`.
        If exact original output is needed, use `--raw`.
        """;

    private const string CoreAgentsSnippet = """
        # tk Global Agent Preset

        Use `tk` as the default compact interface for repo exploration, search, and noisy command output.

        Prefer:
        - `tk changes`
        - `tk tree`
        - `tk files`
        - `tk focus <query> [path]`
        - `tk focus <query> [path] --files-only`
        - `tk view <file>`
        - `tk git status|diff|log`
        - `tk log <file>`

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
        - `tk refs <symbol>` — find all references to a symbol (LSP-backed)
        - `tk refs <file:line:col>` — find references at position
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
            Commands: [new LspStatusCommand(), new RefsCommand(), new LspDaemonCommand()],
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
