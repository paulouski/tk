using Tk.Filters;
using Xunit;

namespace Tk.Tests.Filters;

public class GitLogFilterTests
{
    [Fact]
    public void Non_zero_exit_returns_raw()
    {
        var raw = "fatal: bad revision\n";
        Assert.Equal(raw, new GitLogFilter().Apply(raw, 1));
    }

    [Fact]
    public void Empty_returns_raw()
    {
        Assert.Equal("", new GitLogFilter().Apply("", 0));
    }

    [Fact]
    public void Oneline_format_passes_through_under_limit()
    {
        var raw = "abc1234 first commit\nabc5678 second commit\n";
        Assert.Equal(raw, new GitLogFilter().Apply(raw, 0));
    }

    [Fact]
    public void Oneline_over_50_is_truncated()
    {
        var lines = Enumerable.Range(1, 60).Select(i => $"hash{i:D4} message {i}").ToArray();
        var raw = string.Join("\n", lines) + "\n";
        var actual = new GitLogFilter().Apply(raw, 0);
        var actualLines = actual.Split('\n');
        Assert.Equal("hash0001 message 1", actualLines[0]);
        Assert.Equal("hash0050 message 50", actualLines[49]);
        Assert.Contains("... +", actual);
        Assert.Contains("more lines", actual);
    }

    [Fact]
    public void Full_format_compacted_to_hash_and_first_message_line()
    {
        var raw = """
            commit 1a2b3c4d5e6f7890abcdef1234567890abcdef12
            Author: Jane Doe <jane@example.com>
            Date:   Mon Jan 1 12:00:00 2024 +0000

                Implement feature X

                Body line that should be dropped.

            commit 9876543210fedcba9876543210fedcba98765432
            Merge: aaa bbb
            Author: John <john@example.com>
            Date:   Tue Jan 2 09:00:00 2024 +0000

                Merge pull request #42

                Co-Authored-By: Someone <s@e.com>
            """;
        var actual = new GitLogFilter().Apply(raw, 0);
        Assert.Equal(
            """
            1a2b3c4 Implement feature X
            9876543 Merge pull request #42

            """,
            actual);
    }

    [Fact]
    public void Full_format_message_truncated_at_100_chars()
    {
        var longMsg = new string('x', 150);
        var raw = $"""
            commit aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
            Author: A <a@a>
            Date:   Mon Jan 1 12:00:00 2024 +0000

                {longMsg}
            """;
        var actual = new GitLogFilter().Apply(raw, 0);
        var line = actual.TrimEnd('\n');
        Assert.EndsWith("...", line);
        // Format: "<7 hash> <space> <100 chars> <...>"
        Assert.Equal(7 + 1 + 100 + 3, line.Length);
    }

    [Fact]
    public void Full_format_over_50_commits_appends_more_marker()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 60; i++)
        {
            sb.Append("commit ").Append($"{i:x40}").AppendLine();
            sb.AppendLine("Author: A <a@a>");
            sb.AppendLine("Date:   Mon Jan 1 12:00:00 2024 +0000");
            sb.AppendLine();
            sb.AppendLine($"    msg {i}");
            sb.AppendLine();
        }
        var actual = new GitLogFilter().Apply(sb.ToString(), 0);
        var lines = actual.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(51, lines.Length); // 50 commits + marker
        Assert.Contains("more commits", lines[50]);
    }
}
