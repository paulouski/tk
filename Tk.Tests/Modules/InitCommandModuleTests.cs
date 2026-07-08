using Tk;
using Tk.Commands;
using Tk.Modules;
using Xunit;

namespace Tk.Tests.Modules;

/// <summary>
/// Tests that InitCommand only emits snippets for enabled modules.
/// </summary>
[Collection("HomeSensitive")]
public class InitCommandModuleTests
{
    private static async Task<string> RunInitAndRead(IReadOnlyList<ModuleDescriptor> enabledModules, string relativePath)
    {
        var tempHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempHome);
        var origHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            Environment.SetEnvironmentVariable("HOME", tempHome);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var ctx = new CommandContext([], DetailLevel.Default, raw: false, stdout, stderr,
                new FakeProcessRunner());

            var cmd = new InitCommand(enabledModules);
            await cmd.RunAsync(ctx);

            var path = Path.Combine(tempHome, relativePath);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", origHome);
            Directory.Delete(tempHome, recursive: true);
        }
    }

    private static Task<string> RunInitAndReadClaude(IReadOnlyList<ModuleDescriptor> enabledModules) =>
        RunInitAndRead(enabledModules, Path.Combine(".claude", "CLAUDE.md"));

    private static Task<string> RunInitAndReadOpencode(IReadOnlyList<ModuleDescriptor> enabledModules) =>
        RunInitAndRead(enabledModules, Path.Combine(".config", "opencode", "AGENTS.md"));

    [Fact]
    public async Task All_modules_enabled_includes_unity_snippet()
    {
        var content = await RunInitAndReadClaude(ModuleCatalog.All);

        Assert.Contains("tk unity", content);
    }

    [Fact]
    public async Task All_modules_enabled_includes_dotnet_snippet()
    {
        var content = await RunInitAndReadClaude(ModuleCatalog.All);

        Assert.Contains("tk quality", content);
    }

    [Fact]
    public async Task Unity_disabled_removes_unity_from_output()
    {
        var enabled = ModuleCatalog.All.Where(m => m.Name != "unity").ToList();
        var content = await RunInitAndReadClaude(enabled);

        Assert.DoesNotContain("tk unity", content);
    }

    [Fact]
    public async Task Dotnet_disabled_removes_quality_from_output()
    {
        var enabled = ModuleCatalog.All.Where(m => m.Name != "dotnet").ToList();
        var content = await RunInitAndReadClaude(enabled);

        Assert.DoesNotContain("tk quality", content);
    }

    [Fact]
    public async Task Core_snippet_always_present()
    {
        var coreOnly = ModuleCatalog.All.Where(m => m.AlwaysOn).ToList();
        var content = await RunInitAndReadClaude(coreOnly);

        Assert.Contains("tk changes", content);
        Assert.Contains("tk tree", content);
    }

    [Fact]
    public async Task Output_contains_claude_marker_and_end_marker()
    {
        var content = await RunInitAndReadClaude(ModuleCatalog.All);

        Assert.Contains("<!-- tk-global-claude -->", content);
        Assert.Contains("<!-- /tk-global -->", content);
    }

    [Fact]
    public async Task Opencode_target_written_with_opencode_marker()
    {
        var content = await RunInitAndReadOpencode(ModuleCatalog.All);

        Assert.Contains("<!-- tk-global-opencode -->", content);
        Assert.Contains("<!-- /tk-global -->", content);
    }

    [Fact]
    public async Task Opencode_target_includes_core_agents_snippet()
    {
        var content = await RunInitAndReadOpencode(ModuleCatalog.All);

        Assert.Contains("tk Global Agent Preset", content);
        Assert.Contains("tk changes", content);
    }
}
