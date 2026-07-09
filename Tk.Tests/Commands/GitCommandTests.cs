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
        var ctx = new CommandContext(args, detail, raw, stdout, stderr, runner, commandName: "git");
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
        // UU is a conflict (unmerged), not a plain modification — GitStatusFilter now routes
        // conflicted paths only into the conflict bucket instead of double-counting them into
        // mod= too (see GitStatusFilter.FormatPorcelain's `continue` after IsConflict).
        Assert.Contains("conf=1", output);
        Assert.DoesNotContain("mod=1", output);
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
        // No explicit limit -> tk also probes `rev-list --count` (call 0) before the real
        // `git log` fetch (call 1) to know whether the injected cap hid anything.
        var runner = new FakeProcessRunner()
            .Returns(stdout: "3\n")
            .Returns(stdout: "abc1234 message (1 hour ago) <A>\n");

        await RunAsync(["log"], runner);

        Assert.Equal(["git", "rev-list", "--count", "--no-merges", "HEAD"], runner.Calls[0]);
        Assert.Contains("-10", runner.Calls[1]);
        Assert.Contains("--no-merges", runner.Calls[1]);
        Assert.Contains(runner.Calls[1], arg => arg.StartsWith("--pretty=format:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Log_respects_explicit_limit_and_format()
    {
        var runner = new FakeProcessRunner().Returns(stdout: "abc1234 message\n");

        await RunAsync(["log", "--oneline", "-3"], runner);

        // An explicit user limit skips tk's own cap injection entirely, so no `rev-list --count`
        // probe is needed either -> exactly one process call.
        Assert.Single(runner.Calls);
        Assert.DoesNotContain("-10", runner.Calls[0]);
        Assert.DoesNotContain("--no-merges", runner.Calls[0]);
        Assert.DoesNotContain(runner.Calls[0], arg => arg.StartsWith("--pretty=format:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Log_without_limit_reports_truncated_history_beyond_cap()
    {
        var runner = new FakeProcessRunner()
            .Returns(stdout: "51\n") // rev-list --count: true history size
            .Returns(stdout: string.Join('\n', Enumerable.Range(1, 10).Select(i => $"abc{i:D4} message {i}")) + "\n");

        var (_, output, _) = await RunAsync(["log"], runner);

        // 10 shown out of a real history of 51 -> 41 hidden, reported exactly (not the intra-fetch
        // line delta, which would be 0 since the fetch itself only ever returned 10 lines).
        Assert.Contains("hid=41/51", output);
    }

    [Fact]
    public async Task Log_without_limit_emits_no_signal_when_history_fits_cap()
    {
        var runner = new FakeProcessRunner()
            .Returns(stdout: "7\n") // history smaller than the display cap
            .Returns(stdout: string.Join('\n', Enumerable.Range(1, 7).Select(i => $"abc{i:D4} message {i}")) + "\n");

        var (_, output, _) = await RunAsync(["log"], runner);

        Assert.DoesNotContain("hid=", output);
    }

    [Fact]
    public async Task Log_without_limit_falls_back_gracefully_when_count_call_fails()
    {
        var runner = new FakeProcessRunner()
            .Returns(exitCode: 128, stderr: "fatal: bad revision 'HEAD'\n")
            .Returns(stdout: "abc1234 message (1 hour ago) <A>\n");

        var (exit, output, _) = await RunAsync(["log"], runner);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("hid=", output);
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

    // ─── raw mode and generic passthrough spawn the real "git" binary ──────────
    // Regression: CommandContext strips the leading "git" token from ctx.Args, so
    // passthrough branches must re-add it before spawning a process, or they'd try
    // to exec a program literally named "status"/"stash"/etc.

    [Fact]
    public async Task Raw_status_spawns_real_git_with_status_subcommand()
    {
        var runner = new FakeProcessRunner().Returns(stdout: "On branch main\n");

        await RunAsync(["status"], runner, raw: true);

        Assert.Equal(["git", "status"], runner.Calls[0]);
    }

    [Fact]
    public async Task Raw_log_spawns_real_git_with_log_subcommand()
    {
        var runner = new FakeProcessRunner().Returns(stdout: "abc1234 msg\n");

        await RunAsync(["log"], runner, raw: true);

        Assert.Equal(["git", "log"], runner.Calls[0]);
    }

    [Fact]
    public async Task Raw_mode_preserves_global_git_flags_ahead_of_the_subcommand()
    {
        // Regression for the argument-model fix: raw mode spawns via ctx.OriginalCommandArgs
        // (CommandName + Operands), so global flags like `-C <path>` must survive in order.
        var runner = new FakeProcessRunner().Returns(stdout: "On branch main\n");

        await RunAsync(["-C", "/some/repo", "status"], runner, raw: true);

        Assert.Equal(["git", "-C", "/some/repo", "status"], runner.Calls[0]);
    }

    [Fact]
    public async Task Passthrough_subcommand_preserves_global_git_flags_ahead_of_the_subcommand()
    {
        var runner = new FakeProcessRunner().Returns(exitCode: 0, stdout: "stash list output\n");

        await RunAsync(["-c", "user.name=Test", "stash", "list"], runner);

        Assert.Equal(["git", "-c", "user.name=Test", "stash", "list"], runner.Calls[0]);
    }

    [Theory]
    [InlineData("stash list")]
    [InlineData("branch -a")]
    [InlineData("checkout main")]
    [InlineData("merge feature")]
    [InlineData("rebase --continue")]
    [InlineData("reset --hard")]
    [InlineData("tag -l")]
    [InlineData("add .")]
    [InlineData("commit -m msg")]
    [InlineData("push")]
    [InlineData("pull")]
    [InlineData("fetch")]
    public async Task Unfiltered_subcommands_passthrough_to_real_git(string argsLine)
    {
        var args = argsLine.Split(' ');
        var runner = new FakeProcessRunner().Returns(exitCode: 0, stdout: "ok\n");

        var (exit, output, _) = await RunAsync(args, runner);

        Assert.Equal(0, exit);
        Assert.Equal(["git", .. args], runner.Calls[0]);
        Assert.Equal("ok\n", output);
    }

    [Fact]
    public async Task Unfiltered_subcommand_propagates_nonzero_exit_code()
    {
        var runner = new FakeProcessRunner().Returns(exitCode: 1, stderr: "error: pathspec 'x' did not match\n");

        var (exit, output, _) = await RunAsync(["checkout", "x"], runner);

        Assert.Equal(1, exit);
        Assert.Contains("did not match", output);
    }
}
