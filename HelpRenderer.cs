using System.Text;
using Tk.Modules;

namespace Tk;

/// <summary>
/// Builds the runtime help text, gated on which modules are currently enabled.
/// </summary>
internal static class HelpRenderer
{
    /// <summary>
    /// Builds the full help text for the given set of enabled modules.
    /// The core module is always expected to be present; optional modules
    /// (dotnet, unity, lsp) are included only when their descriptor appears
    /// in <paramref name="enabledModules"/>.
    /// </summary>
    public static string BuildHelp(IReadOnlyList<ModuleDescriptor> enabledModules)
    {
        var enabled = enabledModules.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();

        sb.AppendLine("tk - token killer for Claude Code");
        sb.AppendLine();
        sb.AppendLine("Usage:");
        sb.AppendLine("  tk <command> [args...]     Run command with filtered output");
        sb.AppendLine("  tk --more <command>        Same command, with extra detail");
        sb.AppendLine("  tk --raw <command>         Run command with no tk filtering");
        sb.AppendLine("  tk log <file> [flags]      Read and filter ASP.NET service log");
        sb.AppendLine("  tk view <file[:a-b|::sym]> Compact file view; multiple files, line range, or symbol");
        sb.AppendLine("  tk changes                 Compact repo state card (status + diff)");
        sb.AppendLine("  tk branch [base]           Branch state vs base (commits + diff)");
        sb.AppendLine("  tk tree [path]             Compact repo tree for agents");
        sb.AppendLine("  tk files [path]            Compact file inventory");
        sb.AppendLine("  tk focus <query> [path]    Code-first repo search with top files and samples");
        sb.AppendLine("  tk init                    Install global Claude + AGENTS instructions");
        sb.AppendLine("  tk switch                  Toggle between two Claude Code accounts");
        sb.AppendLine("  tk mv <old> <new>          Move file preserving git history (git mv when tracked)");
        sb.AppendLine();
        sb.AppendLine("Commands:");
        sb.AppendLine("  tk ls <path>                   Names only (no perms/dates/sizes)");
        sb.AppendLine("  tk grep [flags] pat path       Grep summary: match count, top files, samples");
        sb.AppendLine("  tk find <path> [flags]         Find with path prefix stripped");
        sb.AppendLine("  tk view <file[:a-b|::sym]>     File card, line range, or symbol body; multiple files allowed");
        sb.AppendLine("  tk changes                     Repo status card for agent startup");
        sb.AppendLine("  tk branch [base]               Branch vs base (auto: upstream/main/master)");
        sb.AppendLine("  tk tree [path]                 Repo tree with compact depth and counts; add --code");
        sb.AppendLine("  tk files [path]                Key files and top directories; add --code");
        sb.AppendLine("  tk focus <query> [path]        Code-first search; use --code/--docs/--all");
        sb.AppendLine("  tk mv <old> <new>              Move file; git mv when tracked (preserves history), else filesystem");
        sb.AppendLine("  tk module list|enable|disable  Manage enabled modules");
        sb.AppendLine("  tk git status|log|diff|show    Git compact output");
        sb.AppendLine("  tk log <file>                  Filter service log (errors/warnings only)");
        sb.AppendLine("  tk log <file> --errors         Errors only (fail/crit)");
        sb.AppendLine("  tk log <file> --last 20        Last N entries");
        sb.AppendLine("  tk log <file> --all            No filtering, raw output");

        if (enabled.Contains("dotnet"))
        {
            sb.AppendLine("  tk quality [path]              Local analyzers + dotnet build + format check; add --test-filter");
            sb.AppendLine("  tk dotnet build|test|restore   .NET build output (NuGet dedup, CS grouping)");
        }

        if (enabled.Contains("unity"))
        {
            sb.AppendLine("  tk unity tree|files|status     Unity-aware variants: hides Library/Temp/meta files");
        }

        if (enabled.Contains("lsp"))
        {
            sb.AppendLine("  tk refs <symbol>               Find all references to a symbol (LSP-backed)");
            sb.AppendLine("  tk refs <file:line:col>        Find references at position");
            sb.AppendLine("  tk rename <file:line:col> <n>  Rename a symbol everywhere");
            sb.AppendLine("  tk lsp status|stop             LSP daemon status and control");
        }

        sb.AppendLine();
        sb.AppendLine("Any other command passes through unfiltered.");

        // Show hint only when at least one optional module is absent.
        var optionalNames = ModuleCatalog.All.Where(m => !m.AlwaysOn).Select(m => m.Name);
        bool anyDisabled = optionalNames.Any(name => !enabled.Contains(name));
        if (anyDisabled)
        {
            sb.AppendLine();
            sb.Append("Disabled modules are hidden. Run 'tk module list' to see all modules.");
        }

        return sb.ToString();
    }
}
