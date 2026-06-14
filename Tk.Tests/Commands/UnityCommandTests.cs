using Tk;
using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

public class UnityCommandTests
{
    private static async Task<(int ExitCode, string Output, FakeProcessRunner Runner)> RunAsync(
        string[] args,
        FakeProcessRunner runner,
        DetailLevel detail = DetailLevel.Default,
        bool raw = false)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var ctx = new CommandContext(args, detail, raw, stdout, stderr, runner);
        var exit = await new UnityCommand().RunAsync(ctx);
        return (exit, stdout.ToString(), runner);
    }

    [Fact]
    public async Task Status_issues_two_git_calls_in_correct_order()
    {
        var runner = new FakeProcessRunner()
            .Returns(stdout: "On branch main\nnothing to commit\n")
            .Returns(stdout: "## main\nM  Assets/Foo.cs\nM  Assets/Foo.cs.meta\n");

        var (_, _, r) = await RunAsync(["status"], runner);

        Assert.Equal(["git", "status"], r.Calls[0]);
        Assert.Equal(["git", "status", "--porcelain=v1", "--branch"], r.Calls[1]);
    }

    [Fact]
    public async Task Status_output_contains_meta_field()
    {
        var runner = new FakeProcessRunner()
            .Returns(stdout: "On branch main\n")
            .Returns(stdout: "## main\nM  Assets/Foo.cs\nM  Assets/Foo.cs.meta\n");

        var (_, output, _) = await RunAsync(["status"], runner);

        Assert.Contains("meta=1", output);
        Assert.Contains("st=1", output);
    }

    [Fact]
    public async Task Status_plain_failure_is_returned_immediately()
    {
        var runner = new FakeProcessRunner()
            .Returns(exitCode: 128, stderr: "fatal: not a git repository\n");

        var (exit, output, r) = await RunAsync(["status"], runner);

        Assert.Equal(128, exit);
        Assert.Single(r.Calls);
    }

    [Fact]
    public async Task Unknown_subcommand_writes_usage_and_returns_zero()
    {
        var runner = new FakeProcessRunner();

        var (exit, output, _) = await RunAsync(["unknown"], runner);

        Assert.Equal(0, exit);
        Assert.Contains("unity:", output);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task No_subcommand_writes_usage_and_returns_zero()
    {
        var runner = new FakeProcessRunner();

        var (exit, output, _) = await RunAsync([], runner);

        Assert.Equal(0, exit);
        Assert.Contains("unity:", output);
    }

    [Fact]
    public async Task Tree_subcommand_hides_unity_ignored_directories()
    {
        var temp = Directory.CreateTempSubdirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(temp.FullName, "Assets"));
            Directory.CreateDirectory(Path.Combine(temp.FullName, "Library"));
            File.WriteAllText(Path.Combine(temp.FullName, "Assets", "Foo.cs"), "class Foo {}");
            File.WriteAllText(Path.Combine(temp.FullName, "Assets", "Foo.cs.meta"), "meta");

            var runner = new FakeProcessRunner();
            var (exit, output, _) = await RunAsync(["tree", temp.FullName], runner, DetailLevel.Default);

            Assert.Equal(0, exit);
            Assert.DoesNotContain("Library", output);
            Assert.DoesNotContain(".meta", output);
        }
        finally
        {
            Directory.Delete(temp.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task Files_subcommand_hides_meta_files()
    {
        var temp = Directory.CreateTempSubdirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(temp.FullName, "Assets"));
            File.WriteAllText(Path.Combine(temp.FullName, "Assets", "Foo.cs"), "class Foo {}");
            File.WriteAllText(Path.Combine(temp.FullName, "Assets", "Foo.cs.meta"), "meta");

            var runner = new FakeProcessRunner();
            var (exit, output, _) = await RunAsync(["files", temp.FullName], runner, DetailLevel.Default);

            Assert.Equal(0, exit);
            Assert.DoesNotContain(".meta", output);
            Assert.Contains("Foo.cs", output);
        }
        finally
        {
            Directory.Delete(temp.FullName, recursive: true);
        }
    }
}
