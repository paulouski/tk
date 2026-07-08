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

        sb.AppendLine("tk - token killer for AI coding agents");
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
        sb.AppendLine("  tk focus <word...> [-p path] OR-search words (ranked by coverage); quote for exact phrase; --files-only");
        sb.AppendLine("  tk init                    Install global Claude + AGENTS + opencode instructions");
        sb.AppendLine("  tk switch                  Toggle between two Claude Code accounts");
        sb.AppendLine("  tk mv <old> <new>          Move file preserving git history (git mv when tracked)");
        sb.AppendLine();
        sb.AppendLine("Commands:");

        foreach (var module in enabledModules)
        {
            foreach (var row in module.Rows)
            {
                if (row.Usage is null || row.Description is null)
                    continue;

                AppendRow(sb, row.Usage, row.Description);
                if (row.ExtraHelpLines is not null)
                {
                    foreach (var (usage, description) in row.ExtraHelpLines)
                        AppendRow(sb, usage, description);
                }
            }
        }

        sb.AppendLine();
        sb.Append("Any other command passes through unfiltered.");

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

    private static void AppendRow(StringBuilder sb, string usage, string description) =>
        sb.AppendLine("  " + usage.PadRight(31) + description);
}
