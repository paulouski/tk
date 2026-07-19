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
    private static async Task RunInit(IReadOnlyList<ModuleDescriptor> enabledModules, string home)
    {
        var origHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            Environment.SetEnvironmentVariable("HOME", home);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var ctx = new CommandContext([], DetailLevel.Default, raw: false, stdout, stderr,
                new FakeProcessRunner());

            var cmd = new InitCommand(enabledModules);
            Assert.Equal(0, await cmd.RunAsync(ctx));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", origHome);
        }
    }

    private static async Task<string> RunInitAndRead(IReadOnlyList<ModuleDescriptor> enabledModules, string relativePath)
    {
        var tempHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempHome);
        try
        {
            await RunInit(enabledModules, tempHome);

            var path = Path.Combine(tempHome, relativePath);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        finally
        {
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

    [Fact]
    public async Task Codex_lsp_rules_are_idempotent_and_preserve_user_content()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var rulesPath = Path.Combine(tempHome, ".codex", "rules", "tk-lsp.rules");
        Directory.CreateDirectory(Path.GetDirectoryName(rulesPath)!);
        File.WriteAllText(rulesPath, "# user rule\n");

        try
        {
            await RunInit(ModuleCatalog.All, tempHome);
            var first = File.ReadAllText(rulesPath);

            await RunInit(ModuleCatalog.All, tempHome);
            var second = File.ReadAllText(rulesPath);

            Assert.Equal(first, second);
            Assert.StartsWith("# user rule", second);
            Assert.Contains("# tk-lsp-sandbox:start", second);
            Assert.Contains("pattern=[\"tk\", [\"def\", \"refs\", \"callers\", \"calls\", \"impl\", \"sig\", \"sym\", \"diag\"]]", second);
            Assert.Contains("pattern=[\"tk\", \"lsp\", [\"status\", \"stop\"]]", second);
            Assert.Contains("pattern=[\"tk\", [\"fix\", \"rename\", \"mv\"]]", second);
            Assert.Contains("pattern=[\"tk\", [\"--more\", \"--raw\"], [\"fix\", \"rename\", \"mv\"]]", second);
            Assert.Equal(1, second.Split("# tk-lsp-sandbox:start").Length - 1);
        }
        finally
        {
            Directory.Delete(tempHome, recursive: true);
        }
    }
}
