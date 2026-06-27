using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

public class GitCommandTests
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
        var exit = await new GitCommand().RunAsync(ctx);
        return (exit, stdout.ToString(), runner);
    }

    [Fact]
    public async Task Status_without_args_probes_plain_status_for_in_progress_state()
    {
        var runner = new FakeProcessRunner()
            .Returns(stdout: """
                On branch main
                You are currently rebasing branch 'feature' on 'abc123'.
                  (fix conflicts and then run "git rebase --continue")
                Unmerged paths:
                  (use "git add <file>..." to mark resolution)
                """)
            .Returns(stdout: """
                ## main
                UU src/App.cs
                """);

        var (_, output, _) = await RunAsync(["status"], runner);

        Assert.Contains("state=rebase+merge", output);
        Assert.Contains("mod=1", output);
        Assert.Equal(["git", "status"], runner.Calls[0]);
        Assert.Equal(["git", "status", "--porcelain=v1", "--branch"], runner.Calls[1]);
    }

    [Fact]
    public async Task Diff_inserts_path_separator_for_existing_path_args()
    {
        var temp = Directory.CreateTempSubdirectory();
        var file = Path.Combine(temp.FullName, "a.cs");
        File.WriteAllText(file, "x");
        var runner = new FakeProcessRunner().Returns(stdout: "");

        await RunAsync(["diff", file], runner);

        Assert.Equal(["git", "diff", "--", file], runner.Calls[0]);
    }

    [Fact]
    public async Task Diff_stat_is_passed_through_without_compact_filter()
    {
        var runner = new FakeProcessRunner().Returns(stdout: " file.cs | 1 +\n");

        var (_, output, _) = await RunAsync(["diff", "--stat"], runner);

        Assert.Equal(" file.cs | 1 +\n", output);
        Assert.Equal(["git", "diff", "--stat"], runner.Calls[0]);
    }

    [Fact]
    public async Task Log_adds_agent_friendly_defaults_when_user_does_not_set_them()
    {
        var runner = new FakeProcessRunner().Returns(stdout: "abc1234 message (1 hour ago) <A>\n");

        await RunAsync(["log"], runner);

        Assert.Contains("-10", runner.Calls[0]);
        Assert.Contains("--no-merges", runner.Calls[0]);
        Assert.Contains(runner.Calls[0], arg => arg.StartsWith("--pretty=format:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Log_respects_explicit_limit_and_format()
    {
        var runner = new FakeProcessRunner().Returns(stdout: "abc1234 message\n");

        await RunAsync(["log", "--oneline", "-3"], runner);

        Assert.DoesNotContain("-10", runner.Calls[0]);
        Assert.DoesNotContain("--no-merges", runner.Calls[0]);
        Assert.DoesNotContain(runner.Calls[0], arg => arg.StartsWith("--pretty=format:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reducing_diff_emits_hidden_lines_footer()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 5; i++)
        {
            sb.AppendLine($"diff --git a/f{i}.cs b/f{i}.cs");
            sb.AppendLine("index aaa..bbb 100644");
            sb.AppendLine($"--- a/f{i}.cs");
            sb.AppendLine($"+++ b/f{i}.cs");
            sb.AppendLine("@@ -1 +1 @@");
            sb.AppendLine("-old");
            sb.AppendLine("+new");
        }
        var runner = new FakeProcessRunner().Returns(stdout: sb.ToString());

        var (_, output, _) = await RunAsync(["diff"], runner);

        Assert.Contains("hid=", output);
        Assert.Contains("(--more, --raw)", output);
    }

    [Fact]
    public async Task Raw_diff_has_no_hidden_lines_footer()
    {
        var raw = "diff --git a/a.cs b/a.cs\n@@ -1 +1 @@\n-old\n+new\n";
        var runner = new FakeProcessRunner().Returns(stdout: raw);

        var (_, output, _) = await RunAsync(["diff"], runner, raw: true);

        Assert.DoesNotContain("hid=", output);
        Assert.Equal(raw, output);
    }

    [Fact]
    public async Task Summary_flag_strips_from_git_args_and_caps_hunk_preview()
    {
        // Build a diff with more changed lines than the default cap (18) so summary shows overflow.
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 5; i++)
        {
            sb.AppendLine($"diff --git a/f{i}.cs b/f{i}.cs");
            sb.AppendLine("index aaa..bbb 100644");
            sb.AppendLine($"--- a/f{i}.cs");
            sb.AppendLine($"+++ b/f{i}.cs");
            sb.AppendLine("@@ -1 +1 @@");
            sb.AppendLine("-old");
            sb.AppendLine("+new");
        }
        var runner = new FakeProcessRunner().Returns(stdout: sb.ToString());

        var (_, output, _) = await RunAsync(["diff", "--summary"], runner);

        // --summary must NOT be forwarded to git
        Assert.DoesNotContain("--summary", runner.Calls[0]);
        // Summary mode: overflow notice present
        Assert.Contains("hunks more", output);
    }

    [Fact]
    public async Task Show_passes_isShow_so_commit_header_is_emitted()
    {
        var showOutput = """
            commit abc1234def5678
            Author: Test User <test@example.com>
            Date:   Mon Jan 1 00:00:00 2024 +0000

                My commit message.

            diff --git a/a.cs b/a.cs
            index aaa..bbb 100644
            --- a/a.cs
            +++ b/a.cs
            @@ -1 +1 @@
            -old
            +new
            """;
        var runner = new FakeProcessRunner().Returns(stdout: showOutput);

        var (_, output, _) = await RunAsync(["show", "abc1234"], runner);

        Assert.Contains("commit abc1234def5678", output);
        Assert.Contains("My commit message.", output);
        Assert.Contains("-old", output);
        Assert.Contains("+new", output);
    }
}
