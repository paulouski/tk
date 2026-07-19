using Tk;
using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

public class SwitchCommandTests
{
    private static string MakeStateFile(string? state = null)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "active");
        if (state is not null)
            File.WriteAllText(path, state);
        return path;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        FakeProcessRunner runner, string stateFile, string[]? args = null)
    {
        var stdout = new StringWriter();
        var ctx = new CommandContext(
            args ?? [], DetailLevel.Default, raw: false, stdout, TextWriter.Null, runner);
        var exit = await new SwitchCommand(stateFile).RunAsync(ctx);
        return (exit, stdout.ToString());
    }

    private static void AssertClaudeWasNotLaunched(FakeProcessRunner runner) =>
        Assert.DoesNotContain(runner.Calls, call => call.Length > 0 && call[0] == "claude");

    [Fact]
    public async Task First_run_saves_account_1_and_stops_after_logging_out()
    {
        var stateFile = MakeStateFile();
        var runner = new FakeProcessRunner()
            .Returns(stdout: "account-1")
            .Returns()
            .Returns();

        var (exit, output) = await RunAsync(runner, stateFile);

        Assert.Equal(0, exit);
        Assert.Equal("setup", File.ReadAllText(stateFile));
        Assert.Equal(3, runner.Calls.Count);
        Assert.Contains("Log in with `claude` as account 2", output);
        AssertClaudeWasNotLaunched(runner);
    }

    [Fact]
    public async Task Setup_completion_saves_account_2_and_restores_account_1_without_launching_claude()
    {
        var stateFile = MakeStateFile("setup");
        var runner = new FakeProcessRunner()
            .Returns(stdout: "account-2")
            .Returns()
            .Returns(stdout: "account-1")
            .Returns();

        var (exit, output) = await RunAsync(runner, stateFile);

        Assert.Equal(0, exit);
        Assert.Equal("1", File.ReadAllText(stateFile));
        Assert.Equal(4, runner.Calls.Count);
        Assert.Contains("Switched to account 1", output);
        AssertClaudeWasNotLaunched(runner);
    }

    [Theory]
    [InlineData("1", "2", "Claude Code-credentials-1", "Claude Code-credentials-2")]
    [InlineData("2", "1", "Claude Code-credentials-2", "Claude Code-credentials-1")]
    public async Task Toggle_saves_current_account_and_restores_other_without_launching_claude(
        string from, string to, string fromSlot, string toSlot)
    {
        var stateFile = MakeStateFile(from);
        var runner = new FakeProcessRunner()
            .Returns(stdout: $"account-{from}")
            .Returns()
            .Returns(stdout: $"account-{to}")
            .Returns();

        var (exit, output) = await RunAsync(runner, stateFile);

        Assert.Equal(0, exit);
        Assert.Equal(to, File.ReadAllText(stateFile));
        Assert.Contains(fromSlot, runner.Calls[1]);
        Assert.Contains(toSlot, runner.Calls[2]);
        Assert.Contains($"Switched to account {to}", output);
        AssertClaudeWasNotLaunched(runner);
    }

    [Fact]
    public async Task Help_describes_auth_switching_without_auto_launch()
    {
        var runner = new FakeProcessRunner();

        var (exit, output) = await RunAsync(runner, MakeStateFile(), ["--help"]);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("opens Claude Code", output);
        Assert.Empty(runner.Calls);
    }
}
