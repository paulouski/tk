using Tk;
using Tk.Filters;
using Xunit;

namespace Tk.Tests.Filters;

public class FindFilterTests
{
    [Fact]
    public void Empty_with_zero_exit_reports_zero()
    {
        Assert.Equal("find n=0\n", new FindFilter(DetailLevel.Default).Apply("", 0));
    }

    [Fact]
    public void Whitespace_with_non_zero_exit_returns_raw()
    {
        var raw = "  \n";
        Assert.Equal(raw, new FindFilter(DetailLevel.Default).Apply(raw, 1));
    }

    [Fact]
    public void Strips_common_prefix_and_groups_top_dirs()
    {
        var raw = """
            /repo/src/a.cs
            /repo/src/b.cs
            /repo/src/sub/c.cs
            /repo/tests/t1.cs
            /repo/tests/t2.cs
            """;
        var actual = new FindFilter(DetailLevel.Default).Apply(raw, 0);
        Assert.Equal(
            """
            find n=5
            top=src(3),tests(2)
            paths:
              src/a.cs
              src/b.cs
              src/sub/c.cs
              tests/t1.cs
              tests/t2.cs

            """,
            actual);
    }

    [Fact]
    public void Counts_permission_errors_separately()
    {
        var raw = """
            ./a.cs
            find: ‘./locked’: Permission denied
            ./b.cs
            """;
        var actual = new FindFilter(DetailLevel.Default).Apply(raw, 1);
        Assert.Contains("find n=2 err=1", actual);
        Assert.DoesNotContain("Permission denied", actual);
    }

    [Fact]
    public void Default_truncates_to_5_paths_with_more_marker()
    {
        var raw = string.Join('\n', Enumerable.Range(0, 9).Select(i => $"/r/dir/f{i}.cs"));
        var actual = new FindFilter(DetailLevel.Default).Apply(raw, 0);
        Assert.Contains("find n=9", actual);
        Assert.Contains("+4 more paths", actual);
        var pathLines = actual.Split('\n').Where(l => l.StartsWith("  ")).ToArray();
        Assert.Equal(5, pathLines.Length);
    }

    [Fact]
    public void More_detail_extends_path_limit_to_20()
    {
        var raw = string.Join('\n', Enumerable.Range(0, 25).Select(i => $"/r/dir/f{i}.cs"));
        var actual = new FindFilter(DetailLevel.More).Apply(raw, 0);
        var pathLines = actual.Split('\n').Where(l => l.StartsWith("  ")).ToArray();
        Assert.Equal(20, pathLines.Length);
        Assert.Contains("+5 more paths", actual);
    }
}
