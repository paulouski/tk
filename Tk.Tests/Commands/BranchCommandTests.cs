using Tk;
using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

public class BranchCommandTests
{
    private static async Task<(int ExitCode, string Output)> RunAsync(FakeProcessRunner runner)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var ctx = new CommandContext([], DetailLevel.Default, raw: false, stdout, stderr, runner);
        var exit = await new BranchCommand().RunAsync(ctx);
        return (exit, stdout.ToString());
    }

    // Regression: the upstream base label used to print the literal "<branch>@{upstream}"
    // placeholder instead of the resolved ref (e.g. "origin/main").
    [Fact]
    public async Task Upstream_base_resolves_to_real_ref_not_placeholder()
    {
        var runner = new FakeProcessRunner()
            .Returns(exitCode: 0, stdout: "feature\n")     // current branch
            .Returns(exitCode: 0, stdout: "origin/main\n") // resolved upstream via --abbrev-ref
            .Returns(exitCode: 0, stdout: "0\t0\n");       // rev-list --left-right --count

        var (exit, output) = await RunAsync(runner);

        Assert.Equal(0, exit);
        Assert.Contains("base=origin/main", output);
        Assert.DoesNotContain("@{upstream}", output);
    }

    [Fact]
    public async Task No_upstream_falls_back_to_origin_main_candidate()
    {
        var runner = new FakeProcessRunner()
            .Returns(exitCode: 0, stdout: "feature\n") // current branch
            .Returns(exitCode: 1, stdout: "")          // no upstream configured
            .Returns(exitCode: 0, stdout: "")          // origin/main exists (--verify --quiet)
            .Returns(exitCode: 0, stdout: "0\t0\n");   // rev-list --left-right --count

        var (exit, output) = await RunAsync(runner);

        Assert.Equal(0, exit);
        Assert.Contains("base=origin/main", output);
    }
}
