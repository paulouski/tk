using Tk.Commands;
using Tk.Filters;

namespace Tk.Modules;

/// <summary>
/// Single source of truth for all registered modules and their command rows. Each row
/// declares enough to drive builtin dispatch, external-filter resolution, generated help,
/// and module gating for both kinds of command (see CommandRow, ModuleDescriptor).
/// </summary>
public static class ModuleCatalog
{
    private static IOutputFilter ResolveDotnetFilter(string[] args, DetailLevel level)
    {
        var subcommand = FilterRegistry.FindDotnetSubcommand(args);
        return subcommand?.ToLowerInvariant() switch
        {
            "build" => new DotnetBuildFilter(),
            "test" => new DotnetTestFilter(level),
            "restore" => new DotnetRestoreFilter(),
            _ => new PassthroughFilter()
        };
    }

    private static Func<string[], DetailLevel, IOutputFilter> MakeGrepResolver(string command) =>
        (args, level) => new GrepFilter(command, level, FilterRegistry.FindSearchPattern(args, command));

    private static IOutputFilter ResolveFindFilter(string[] args, DetailLevel level) => new FindFilter(level);

    public static IReadOnlyList<ModuleDescriptor> All { get; } =
    [
        new ModuleDescriptor(
            Name: "core",
            Rows:
            [
                CommandRow.Builtin(new LsCommand(),
                    "tk ls <path>", "Names only (no perms/dates/sizes)"),
                CommandRow.External("grep", MakeGrepResolver("grep"),
                    "tk grep [flags] pat path", "Grep summary: match count, top files, samples"),
                CommandRow.External("find", ResolveFindFilter,
                    "tk find <path> [flags]", "Find with path prefix stripped"),
                CommandRow.Builtin(new ViewCommand(),
                    "tk view <file[:a-b|::sym]>", "File card, line range, or symbol body; multiple files allowed"),
                CommandRow.Builtin(new ChangesCommand(),
                    "tk changes", "Repo status card for agent startup"),
                CommandRow.Builtin(new BranchCommand(),
                    "tk branch [base]", "Branch vs base (auto: upstream, origin/main|origin/master, main|master)"),
                CommandRow.Builtin(new TreeCommand(),
                    "tk tree [path]", "Repo tree with compact depth and counts; add --code"),
                CommandRow.Builtin(new FilesCommand(),
                    "tk files [path]", "Key files and top directories; add --code"),
                CommandRow.Builtin(new FocusCommand(),
                    "tk focus <word...> [-p path]", "Multi-word OR search ranked by coverage; quote for phrase; --code/--docs/--all/--files-only"),
                CommandRow.Builtin(new MvCommand(),
                    "tk mv <old> <new>", "Move file; git mv when tracked (preserves history), else filesystem. For .cs files moved across folders, fixes the namespace and referencing files by default (needs lsp module)",
                    extraHelpLines:
                    [
                        ("tk mv <old> <new> --no-fix-ns", "Skip the automatic namespace fix; move only"),
                        ("tk mv <old> <new> --ns <namespace>", "Use this namespace instead of the computed convention"),
                    ]),
                CommandRow.Builtin(new ModuleCommand(),
                    "tk module list|enable|disable", "Manage enabled modules"),
                CommandRow.Builtin(new GitCommand(),
                    "tk git status|log|diff|show", "Git compact output"),
                CommandRow.Builtin(new LogCommand(),
                    "tk log <file>", "Filter service log: warn/fail/crit in full + top info groups",
                    extraHelpLines:
                    [
                        ("tk --more log <file>", "Same, but every deduped info group (not just the top ones)"),
                        ("tk log <file> --errors", "Errors only (fail/crit)"),
                        ("tk log <file> --last 20", "Last N entries"),
                        ("tk log <file> --all", "No filtering, raw output"),
                    ]),
                CommandRow.Builtin(new InitCommand()),
                CommandRow.Builtin(new SwitchCommand()),
                CommandRow.External("rg", MakeGrepResolver("rg")),
            ],
            AlwaysOn: true,
            InitSnippet: ModuleSnippets.CoreSnippet),

        new ModuleDescriptor(
            Name: "dotnet",
            Rows:
            [
                CommandRow.Builtin(new QualityCommand(),
                    "tk quality [path]", "Local analyzers + dotnet build + format check; add --test-filter"),
                CommandRow.External("dotnet", ResolveDotnetFilter,
                    "tk dotnet build|test|restore", ".NET build output (NuGet dedup, CS grouping)"),
            ],
            AlwaysOn: false,
            InitSnippet: ModuleSnippets.DotnetSnippet),

        new ModuleDescriptor(
            Name: "unity",
            Rows:
            [
                CommandRow.Builtin(new UnityCommand(),
                    "tk unity tree|files|status", "Unity-aware variants: hides Library/Temp/meta files"),
            ],
            AlwaysOn: false,
            InitSnippet: ModuleSnippets.UnitySnippet),

        new ModuleDescriptor(
            Name: "lsp",
            Rows:
            [
                CommandRow.Builtin(new DiagCommand(),
                    "tk diag <file|project|dir>", "Compile diagnostics from the warm LSP daemon (no build)",
                    extraHelpLines:
                    [
                        ("tk diag <path> --errors", "Errors only"),
                        ("tk diag --changed", "Diagnostics for changed .cs files (staged+modified+untracked)"),
                    ]),
                CommandRow.Builtin(new RefsCommand(),
                    "tk refs <symbol>", "Find all references to a symbol (LSP-backed)",
                    extraHelpLines: [("tk refs <file:line:col>", "Find references at position")]),
                CommandRow.Builtin(new DefCommand(),
                    "tk def <symbol>", "Find where a symbol is defined",
                    extraHelpLines: [("tk def <file:line:col>", "Go to definition at position")]),
                CommandRow.Builtin(new ImplCommand(),
                    "tk impl <symbol>", "Find implementations of an interface/abstract member",
                    extraHelpLines: [("tk impl <file:line:col>", "Find implementations at position")]),
                CommandRow.Builtin(new CallersCommand(),
                    "tk callers <symbol>", "Find incoming callers of a symbol",
                    extraHelpLines: [("tk callers <file:line:col>", "Find incoming callers at position")]),
                CommandRow.Builtin(new CallsCommand(),
                    "tk calls <symbol>", "Find outgoing calls made by a symbol (call hierarchy); some servers don't implement this direction",
                    extraHelpLines: [("tk calls <file:line:col>", "Find outgoing calls at position")]),
                CommandRow.Builtin(new SigCommand(),
                    "tk sig <symbol>", "Signature + doc-comment summary for a symbol (hover)",
                    extraHelpLines: [("tk sig <file:line:col>", "Signature at position")]),
                CommandRow.Builtin(new SymCommand(),
                    "tk sym <query>", "Fuzzy workspace-wide symbol search, grouped by file"),
                CommandRow.Builtin(new FixCommand(),
                    "tk fix <file>", "Apply safe quick-fixes only: add missing using / remove unnecessary using"),
                CommandRow.Builtin(new RenameCommand(),
                    "tk rename <file:line:col> <n>", "Rename a symbol everywhere"),
                CommandRow.Builtin(new LspStatusCommand(),
                    "tk lsp status|stop", "LSP daemon status and control"),
                CommandRow.Builtin(new LspDaemonCommand()),
            ],
            AlwaysOn: false,
            InitSnippet: ModuleSnippets.LspSnippet),
    ];

    /// <summary>
    /// Returns the AGENTS.md snippet for a module. Returns null if the module has no agents snippet.
    /// The core module has its own agents-flavored snippet; others reuse the same snippet.
    /// </summary>
    public static string? GetAgentsSnippet(ModuleDescriptor module) => ModuleSnippets.GetAgentsSnippet(module);
}
