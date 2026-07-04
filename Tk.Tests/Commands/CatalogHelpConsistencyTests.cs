using Tk.Modules;
using Xunit;

namespace Tk.Tests.Commands;

/// <summary>
/// The "docs can't drift" guarantee of the command catalog: every catalog row that declares
/// a help line appears in rendered help for its module, disappears when the module is
/// disabled, and every builtin command is represented by a catalog row.
/// </summary>
public class CatalogHelpConsistencyTests
{
    [Fact]
    public void Every_row_with_help_text_appears_in_full_help()
    {
        var help = HelpRenderer.BuildHelp(ModuleCatalog.All);

        foreach (var module in ModuleCatalog.All)
        foreach (var row in module.Rows)
        {
            if (row.Usage is null || row.Description is null)
                continue;

            Assert.Contains(row.Usage, help);
            Assert.Contains(row.Description, help);
            if (row.ExtraHelpLines is null) continue;
            foreach (var (usage, description) in row.ExtraHelpLines)
            {
                Assert.Contains(usage, help);
                Assert.Contains(description, help);
            }
        }
    }

    [Fact]
    public void Disabled_module_rows_are_absent_from_help()
    {
        var withoutOptional = ModuleCatalog.All.Where(m => m.AlwaysOn).ToList();
        var help = HelpRenderer.BuildHelp(withoutOptional);

        foreach (var module in ModuleCatalog.All.Where(m => !m.AlwaysOn))
        foreach (var row in module.Rows)
        {
            if (row.Usage is null) continue;
            Assert.DoesNotContain(row.Usage, help);
        }
    }

    [Fact]
    public void Every_builtin_command_has_a_catalog_row()
    {
        foreach (var module in ModuleCatalog.All)
        foreach (var command in module.Commands)
        {
            Assert.Contains(module.Rows, r =>
                r.Kind == CommandRowKind.Builtin && r.Name == command.Name);
        }
    }

    [Fact]
    public void Row_names_are_unique_across_enabled_set()
    {
        var names = ModuleCatalog.All
            .SelectMany(m => m.Rows)
            .GroupBy(r => (r.Name, r.Kind))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(names);
    }
}
