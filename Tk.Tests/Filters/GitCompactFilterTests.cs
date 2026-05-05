using Tk.Filters;
using Xunit;

namespace Tk.Tests.Filters;

public class GitCompactFilterTests
{
    [Fact]
    public void Empty_with_zero_exit_returns_ok()
    {
        var actual = new GitCompactFilter().Apply("", 0);
        Assert.Equal("ok\n", actual);
    }

    [Fact]
    public void Whitespace_only_with_zero_exit_returns_ok()
    {
        var actual = new GitCompactFilter().Apply("   \n\n", 0);
        Assert.Equal("ok\n", actual);
    }

    [Fact]
    public void Short_output_passes_through()
    {
        var raw = "Switched to branch 'main'\n";
        var actual = new GitCompactFilter().Apply(raw, 0);
        Assert.Equal(raw, actual);
    }

    [Fact]
    public void Five_lines_passes_through()
    {
        var raw = "l1\nl2\nl3\nl4\nl5\n";
        var actual = new GitCompactFilter().Apply(raw, 0);
        Assert.Equal(raw, actual);
    }

    [Fact]
    public void More_than_five_lines_keeps_head_and_tail_with_omission_marker()
    {
        var raw = "a\nb\nc\nd\ne\nf\ng\nh\n";
        var actual = new GitCompactFilter().Apply(raw, 0);
        Assert.Equal(
            """
            a
            b
            c
            ... (3 lines omitted)
            g
            h

            """,
            actual);
    }

    [Fact]
    public void Non_zero_exit_with_empty_output_returns_raw()
    {
        var actual = new GitCompactFilter().Apply("", 1);
        Assert.Equal("", actual);
    }
}
