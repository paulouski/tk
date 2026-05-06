using Tk;
using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

public class ChangesCommandTests
{
    private static async Task<(int ExitCode, string Output)> RunAsync(
        FakeProcessRunner runner, bool raw = false, DetailLevel detail = DetailLevel.Default)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var ctx = new CommandContext(args: [], detail, raw, stdout, stderr, runner);
        var exit = await new ChangesCommand().RunAsync(ctx);
        return (exit, stdout.ToString());
    }

    [Fact]
    public async Task Returns_status_summary_and_skips_diff_when_clean()
    {
        var runner = new FakeProcessRunner()
            .Returns(stdout: "## main...origin/main\n")
            .Returns(stdout: "");
        var (exit, output) = await RunAsync(runner);

        Assert.Equal(0, exit);
        Assert.Contains("ok status st=0 mod=0 untr=0 br=main", output);
        Assert.DoesNotContain("diff", output);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(new[] { "git", "status", "--porcelain=v1", "--branch" }, runner.Calls[0]);
        Assert.Equal(new[] { "git", "diff" }, runner.Calls[1]);
    }

    [Fact]
    public async Task Includes_diff_summary_when_dirty()
    {
        var runner = new FakeProcessRunner()
            .Returns(stdout: "## main\n M src/a.cs\n")
            .Returns(stdout: """
                diff --git a/src/a.cs b/src/a.cs
                index aaa..bbb 100644
                --- a/src/a.cs
                +++ b/src/a.cs
                @@ -1 +1 @@
                -old
                +new
                """);
        var (exit, output) = await RunAsync(runner);

        Assert.Equal(0, exit);
        Assert.Contains("status st=0 mod=1", output);
        Assert.Contains("diff f=1 +1 -1", output);
    }

    [Fact]
    public async Task Status_failure_short_circuits_and_returns_raw()
    {
        var runner = new FakeProcessRunner()
            .Returns(exitCode: 128, stderr: "fatal: not a git repository\n");
        var (exit, output) = await RunAsync(runner);

        Assert.Equal(128, exit);
        Assert.Contains("not a git repository", output);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Raw_mode_concatenates_unfiltered_status_and_diff()
    {
        var runner = new FakeProcessRunner()
            .Returns(stdout: "## main\n M a.cs\n")
            .Returns(stdout: "diff --git a/a.cs b/a.cs\n");
        var (exit, output) = await RunAsync(runner, raw: true);

        Assert.Equal(0, exit);
        Assert.Contains("## main", output);
        Assert.Contains(" M a.cs", output);
        Assert.Contains("diff --git", output);
    }
}
