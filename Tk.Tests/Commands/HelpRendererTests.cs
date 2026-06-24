using Tk;
using Tk.Modules;
using Xunit;

namespace Tk.Tests.Commands;

public class HelpRendererTests
{
    [Fact]
    public void All_modules_enabled_includes_dotnet_commands()
    {
        var help = HelpRenderer.BuildHelp(ModuleCatalog.All);

        Assert.Contains("tk dotnet", help);
        Assert.Contains("tk quality", help);
    }

    [Fact]
    public void All_modules_enabled_includes_unity_command()
    {
        var help = HelpRenderer.BuildHelp(ModuleCatalog.All);

        Assert.Contains("tk unity", help);
    }

    [Fact]
    public void All_modules_enabled_includes_lsp_commands()
    {
        var help = HelpRenderer.BuildHelp(ModuleCatalog.All);

        Assert.Contains("tk refs", help);
        Assert.Contains("tk rename", help);
        Assert.Contains("tk lsp", help);
    }

    [Fact]
    public void Core_only_does_not_include_dotnet_commands()
    {
        var coreOnly = ModuleCatalog.All.Where(m => m.AlwaysOn).ToList();
        var help = HelpRenderer.BuildHelp(coreOnly);

        Assert.DoesNotContain("tk dotnet", help);
        Assert.DoesNotContain("tk quality", help);
    }

    [Fact]
    public void Core_only_does_not_include_unity_command()
    {
        var coreOnly = ModuleCatalog.All.Where(m => m.AlwaysOn).ToList();
        var help = HelpRenderer.BuildHelp(coreOnly);

        Assert.DoesNotContain("tk unity", help);
    }

    [Fact]
    public void Core_only_does_not_include_lsp_commands()
    {
        var coreOnly = ModuleCatalog.All.Where(m => m.AlwaysOn).ToList();
        var help = HelpRenderer.BuildHelp(coreOnly);

        Assert.DoesNotContain("tk refs", help);
        Assert.DoesNotContain("tk rename", help);
        Assert.DoesNotContain("tk lsp", help);
    }

    [Fact]
    public void Core_only_still_includes_core_commands()
    {
        var coreOnly = ModuleCatalog.All.Where(m => m.AlwaysOn).ToList();
        var help = HelpRenderer.BuildHelp(coreOnly);

        Assert.Contains("tk tree", help);
        Assert.Contains("tk mv", help);
        Assert.Contains("tk git", help);
    }

    [Fact]
    public void Disabled_modules_hint_shown_when_optional_module_absent()
    {
        var coreOnly = ModuleCatalog.All.Where(m => m.AlwaysOn).ToList();
        var help = HelpRenderer.BuildHelp(coreOnly);

        Assert.Contains("Disabled modules are hidden", help);
        Assert.Contains("tk module list", help);
    }

    [Fact]
    public void Disabled_modules_hint_absent_when_all_modules_enabled()
    {
        var help = HelpRenderer.BuildHelp(ModuleCatalog.All);

        Assert.DoesNotContain("Disabled modules are hidden", help);
    }

    [Fact]
    public void Help_trigger_includes_help_word()
    {
        // Regression: "help" should be recognized; verify BuildHelp returns non-empty content.
        var help = HelpRenderer.BuildHelp(ModuleCatalog.All);

        Assert.StartsWith("tk - token killer", help);
    }
}
