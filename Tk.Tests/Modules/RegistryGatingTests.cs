using Tk.Commands;
using Tk.Modules;
using Xunit;

namespace Tk.Tests.Modules;

public class RegistryGatingTests
{
    private static BuiltinRegistry BuildRegistry(IEnumerable<ModuleDescriptor> enabled) =>
        new(enabled.SelectMany(m => m.Commands));

    [Fact]
    public void When_unity_disabled_unity_command_is_not_resolvable()
    {
        var enabled = ModuleCatalog.All.Where(m => m.Name != "unity");
        var registry = BuildRegistry(enabled);

        Assert.False(registry.TryResolve("unity", out _));
    }

    [Fact]
    public void When_unity_enabled_unity_command_is_resolvable()
    {
        var registry = BuildRegistry(ModuleCatalog.All);

        Assert.True(registry.TryResolve("unity", out _));
    }

    [Fact]
    public void Core_commands_are_always_resolvable()
    {
        var coreCommands = new[] { "changes", "branch", "switch", "module", "init", "view", "ls", "tree", "files", "focus", "git", "log" };
        var registry = BuildRegistry(ModuleCatalog.All.Where(m => m.AlwaysOn));

        foreach (var name in coreCommands)
            Assert.True(registry.TryResolve(name, out _), $"core command '{name}' should always be resolvable");
    }

    [Fact]
    public void When_dotnet_disabled_quality_command_is_not_resolvable()
    {
        var enabled = ModuleCatalog.All.Where(m => m.Name != "dotnet");
        var registry = BuildRegistry(enabled);

        Assert.False(registry.TryResolve("quality", out _));
    }

    [Fact]
    public void Default_all_enabled_matches_original_command_set()
    {
        var registry = BuildRegistry(ModuleCatalog.All);
        var expectedNames = new[] { "ls", "view", "init", "log", "tree", "files", "changes", "branch", "git", "focus", "switch", "quality", "unity", "module" };

        foreach (var name in expectedNames)
            Assert.True(registry.TryResolve(name, out _), $"command '{name}' should resolve when all modules enabled");
    }

    [Fact]
    public void When_lsp_disabled_refs_and_lsp_commands_are_not_resolvable()
    {
        var enabled = ModuleCatalog.All.Where(m => m.Name != "lsp");
        var registry = BuildRegistry(enabled);

        Assert.False(registry.TryResolve("refs", out _));
        Assert.False(registry.TryResolve("lsp", out _));
    }

    [Fact]
    public void When_lsp_enabled_refs_and_lsp_commands_are_resolvable()
    {
        var registry = BuildRegistry(ModuleCatalog.All);

        Assert.True(registry.TryResolve("refs", out _));
        Assert.True(registry.TryResolve("lsp", out _));
    }
}
