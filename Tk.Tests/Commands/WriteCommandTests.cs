using System.Security.Cryptography;
using Tk;
using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

public class WriteCommandTests
{
    // ─── helpers ────────────────────────────────────────────────────────────

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string[] args, string stdin)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var ctx = new CommandContext(args, DetailLevel.Default, raw: false, stdout, stderr, new FakeProcessRunner());
        var previousIn = Console.In;
        Console.SetIn(new StringReader(stdin));
        try
        {
            var exit = await new WriteCommand().RunAsync(ctx);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetIn(previousIn);
        }
    }

    private static string MakeTempFile(string content)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "file.txt");
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha256Of(string content) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    // ─── multi-line replace ──────────────────────────────────────────────────

    [Fact]
    public async Task Multiline_replace_swaps_the_range_and_reports_counts()
    {
        var path = MakeTempFile("a\nb\nc\nd\ne\n");

        var (exit, output, _) = await RunAsync(
            [path, "2-3", "--expect-first", "b"], "x\ny\nz\n");

        Assert.Equal(0, exit);
        Assert.Contains("ok write", output);
        Assert.Contains("2-3", output);
        Assert.Contains("(-2 +3)", output);
        Assert.Equal("a\nx\ny\nz\nd\ne\n", File.ReadAllText(path));
    }

    // ─── single line (no dash) ───────────────────────────────────────────────

    [Fact]
    public async Task Single_line_replace_without_dash()
    {
        var path = MakeTempFile("a\nb\nc\n");

        var (exit, _, _) = await RunAsync([path, "2", "--expect-first", "b"], "B\n");

        Assert.Equal(0, exit);
        Assert.Equal("a\nB\nc\n", File.ReadAllText(path));
    }

    // ─── insertion (end == start-1) ──────────────────────────────────────────

    [Fact]
    public async Task Insertion_before_a_line_removes_nothing()
    {
        var path = MakeTempFile("a\nb\nc\n");

        var (exit, output, _) = await RunAsync(
            [path, "2-1", "--expect-first", "b"], "NEW\n");

        Assert.Equal(0, exit);
        Assert.Contains("(-0 +1)", output);
        Assert.Equal("a\nNEW\nb\nc\n", File.ReadAllText(path));
    }

    // ─── append at EOF ((n+1)-(n)) ────────────────────────────────────────────

    [Fact]
    public async Task Append_at_eof_with_expect_sha()
    {
        var content = "a\nb\nc\n";
        var path = MakeTempFile(content);
        var sha = Sha256Of(content);

        var (exit, output, _) = await RunAsync(
            [path, "4-3", "--expect-sha", sha], "d\n");

        Assert.Equal(0, exit);
        Assert.Contains("(-0 +1)", output);
        Assert.Equal("a\nb\nc\nd\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task Append_expect_first_is_rejected()
    {
        var path = MakeTempFile("a\nb\nc\n");

        var (exit, _, err) = await RunAsync([path, "4-3", "--expect-first", "a"], "d\n");

        Assert.NotEqual(0, exit);
        Assert.Contains("--expect-first does not apply to an append", err);
        Assert.Equal("a\nb\nc\n", File.ReadAllText(path));
    }

    // ─── out of bounds ────────────────────────────────────────────────────────

    [Fact]
    public async Task Out_of_bounds_range_fails_and_leaves_file_unchanged()
    {
        var content = "a\nb\nc\n";
        var path = MakeTempFile(content);

        var (exit, _, err) = await RunAsync([path, "2-5", "--expect-first", "b"], "x\n");

        Assert.NotEqual(0, exit);
        Assert.Contains("out of bounds", err);
        Assert.Equal(content, File.ReadAllText(path));
    }

    // ─── missing anchor ───────────────────────────────────────────────────────

    [Fact]
    public async Task Missing_anchor_fails_and_leaves_file_unchanged()
    {
        var content = "a\nb\nc\n";
        var path = MakeTempFile(content);

        var (exit, _, err) = await RunAsync([path, "2"], "x\n");

        Assert.NotEqual(0, exit);
        Assert.Contains("requires an anchor", err);
        Assert.Equal(content, File.ReadAllText(path));
    }

    // ─── --expect-first ───────────────────────────────────────────────────────

    [Fact]
    public async Task Expect_first_mismatch_is_fail_closed()
    {
        var content = "a\nb\nc\n";
        var path = MakeTempFile(content);

        var (exit, _, err) = await RunAsync([path, "2", "--expect-first", "ZZZ"], "x\n");

        Assert.NotEqual(0, exit);
        Assert.Contains("first anchor mismatch", err);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public async Task Expect_last_mismatch_is_fail_closed()
    {
        var content = "a\nb\nc\n";
        var path = MakeTempFile(content);

        var (exit, _, err) = await RunAsync([path, "2-3", "--expect-last", "ZZZ"], "x\n");

        Assert.NotEqual(0, exit);
        Assert.Contains("last anchor mismatch", err);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public async Task Expect_last_on_insertion_range_is_rejected()
    {
        var path = MakeTempFile("a\nb\nc\n");

        var (exit, _, err) = await RunAsync([path, "2-1", "--expect-last", "b"], "x\n");

        Assert.NotEqual(0, exit);
        Assert.Contains("--expect-last does not apply to an insertion range", err);
    }

    // ─── --expect-sha ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Expect_sha_match_succeeds()
    {
        var content = "a\nb\nc\n";
        var path = MakeTempFile(content);
        var sha = Sha256Of(content);

        var (exit, _, _) = await RunAsync([path, "2", "--expect-sha", sha], "B\n");

        Assert.Equal(0, exit);
        Assert.Equal("a\nB\nc\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task Expect_sha_mismatch_is_fail_closed()
    {
        var content = "a\nb\nc\n";
        var path = MakeTempFile(content);

        var (exit, _, err) = await RunAsync(
            [path, "2", "--expect-sha", "0000000"], "B\n");

        Assert.NotEqual(0, exit);
        Assert.Contains("sha anchor mismatch", err);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public async Task Expect_sha_accepts_a_short_prefix()
    {
        var content = "a\nb\nc\n";
        var path = MakeTempFile(content);
        var shaPrefix = Sha256Of(content)[..8];

        var (exit, _, _) = await RunAsync([path, "2", "--expect-sha", shaPrefix], "B\n");

        Assert.Equal(0, exit);
        Assert.Equal("a\nB\nc\n", File.ReadAllText(path));
    }
}
