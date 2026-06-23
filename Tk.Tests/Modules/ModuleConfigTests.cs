using Tk.Modules;
using Xunit;

namespace Tk.Tests.Modules;

public class ModuleConfigTests
{
    private static string TempConfigPath() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    [Fact]
    public void Missing_file_enables_all_modules()
    {
        var path = TempConfigPath(); // does not exist
        var config = ModuleConfig.Load(path);

        foreach (var module in ModuleCatalog.All)
            Assert.True(config.IsEnabled(module), $"module '{module.Name}' should be enabled by default");
    }

    [Fact]
    public void Core_is_always_enabled_regardless_of_config()
    {
        var path = TempConfigPath();
        File.WriteAllLines(path, []); // empty file — nothing enabled

        var config = ModuleConfig.Load(path);
        var core = ModuleCatalog.All.Single(m => m.Name == "core");

        Assert.True(config.IsEnabled(core));
    }

    [Fact]
    public void Round_trip_enable_then_load_shows_module_enabled()
    {
        var path = TempConfigPath();
        try
        {
            ModuleConfig.Enable("unity", path);
            var config = ModuleConfig.Load(path);
            var unity = ModuleCatalog.All.Single(m => m.Name == "unity");

            Assert.True(config.IsEnabled(unity));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Round_trip_disable_then_load_shows_module_disabled()
    {
        var path = TempConfigPath();
        try
        {
            // Pre-populate with unity enabled, then disable it.
            File.WriteAllLines(path, ["unity", "dotnet"]);
            ModuleConfig.Disable("unity", path);
            var config = ModuleConfig.Load(path);
            var unity = ModuleCatalog.All.Single(m => m.Name == "unity");

            Assert.False(config.IsEnabled(unity));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Disable_core_returns_false_and_does_not_modify_file()
    {
        var path = TempConfigPath();
        try
        {
            File.WriteAllLines(path, ["unity"]);
            var result = ModuleConfig.Disable("core", path);

            Assert.False(result);
            Assert.Equal(["unity"], File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Enable_is_idempotent()
    {
        var path = TempConfigPath();
        try
        {
            ModuleConfig.Enable("unity", path);
            ModuleConfig.Enable("unity", path);
            var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

            Assert.Single(lines);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Disable_without_existing_file_materialises_file_with_others_enabled()
    {
        var path = TempConfigPath();
        try
        {
            ModuleConfig.Disable("unity", path);

            Assert.True(File.Exists(path));
            var config = ModuleConfig.Load(path);
            var unity = ModuleCatalog.All.Single(m => m.Name == "unity");
            var dotnet = ModuleCatalog.All.Single(m => m.Name == "dotnet");

            Assert.False(config.IsEnabled(unity));
            Assert.True(config.IsEnabled(dotnet));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
