using Tk;
using Tk.Commands;
using Tk.Modules;
using Xunit;

namespace Tk.Tests.Modules;

[Collection("HomeSensitive")]
public class ModuleCommandTests
{
    private static async Task<(int ExitCode, string Out, string Err)> RunAsync(string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var ctx = new CommandContext(args, DetailLevel.Default, raw: false, stdout, stderr,
            new FakeProcessRunner());
        var exit = await new ModuleCommand().RunAsync(ctx);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task List_outputs_all_known_modules()
    {
        var (exit, output, _) = await RunAsync(["list"]);

        Assert.Equal(0, exit);
        Assert.Contains("module=core", output);
        Assert.Contains("module=dotnet", output);
        Assert.Contains("module=unity", output);
    }

    [Fact]
    public async Task List_marks_core_as_always_on()
    {
        var (_, output, _) = await RunAsync(["list"]);

        Assert.Contains("always-on", output);
    }

    [Fact]
    public async Task No_subcommand_prints_usage_and_returns_zero()
    {
        var (exit, output, _) = await RunAsync([]);

        Assert.Equal(0, exit);
        Assert.Contains("module:", output);
    }

    [Fact]
    public async Task Unknown_subcommand_prints_usage_and_returns_zero()
    {
        var (exit, output, _) = await RunAsync(["bogus"]);

        Assert.Equal(0, exit);
        Assert.Contains("module:", output);
    }

    [Fact]
    public async Task Enable_without_name_returns_error()
    {
        var (exit, _, err) = await RunAsync(["enable"]);

        Assert.Equal(1, exit);
        Assert.Contains("module name required", err);
    }

    [Fact]
    public async Task Enable_unknown_module_returns_error()
    {
        var (exit, _, err) = await RunAsync(["enable", "nonexistent"]);

        Assert.Equal(1, exit);
        Assert.Contains("unknown module", err);
    }

    [Fact]
    public async Task Disable_without_name_returns_error()
    {
        var (exit, _, err) = await RunAsync(["disable"]);

        Assert.Equal(1, exit);
        Assert.Contains("module name required", err);
    }

    [Fact]
    public async Task Disable_core_returns_error()
    {
        var (exit, _, err) = await RunAsync(["disable", "core"]);

        Assert.Equal(1, exit);
        Assert.Contains("cannot disable core", err);
    }

    [Fact]
    public async Task Disable_unknown_module_returns_error()
    {
        var (exit, _, err) = await RunAsync(["disable", "nonexistent"]);

        Assert.Equal(1, exit);
        Assert.Contains("unknown module", err);
    }

    [Fact]
    public async Task Enable_known_module_returns_success()
    {
        // This test calls the real ModuleConfig.Enable on disk; we use a temp approach
        // by verifying only the output contract (not persistence), since ModuleConfig tests cover persistence.
        // We can verify it does not crash and returns 0 for a valid module name.
        // To avoid side effects on the real config, we patch the home via environment.
        var origHome = Environment.GetEnvironmentVariable("HOME");
        var tempHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(tempHome);
            Environment.SetEnvironmentVariable("HOME", tempHome);

            var (exit, output, err) = await RunAsync(["enable", "unity"]);

            Assert.Equal(0, exit);
            Assert.Contains("module=unity", output);
            Assert.Contains("enabled", output);
            Assert.Empty(err);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", origHome);
            Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public async Task Disable_known_non_core_module_returns_success()
    {
        var origHome = Environment.GetEnvironmentVariable("HOME");
        var tempHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(tempHome);
            Environment.SetEnvironmentVariable("HOME", tempHome);

            var (exit, output, err) = await RunAsync(["disable", "unity"]);

            Assert.Equal(0, exit);
            Assert.Contains("module=unity", output);
            Assert.Contains("disabled", output);
            Assert.Empty(err);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", origHome);
            Directory.Delete(tempHome, recursive: true);
        }
    }
}
