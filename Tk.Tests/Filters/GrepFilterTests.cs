using Tk;
using Tk.Filters;
using Xunit;

namespace Tk.Tests.Filters;

public class GrepFilterTests
{
    [Fact]
    public void Empty_with_zero_exit_reports_zero()
    {
        Assert.Equal("grep m=0 f=0\n", new GrepFilter("grep", DetailLevel.Default).Apply("", 0));
    }

    [Fact]
    public void Empty_with_no_match_exit_one_reports_zero()
    {
        Assert.Equal("rg m=0 f=0\n", new GrepFilter("rg", DetailLevel.Default).Apply("", 1));
    }

    [Fact]
    public void Whitespace_with_error_exit_returns_raw()
    {
        var raw = "   \n";
        Assert.Equal(raw, new GrepFilter("rg", DetailLevel.Default).Apply(raw, 2));
    }

    [Fact]
    public void Lines_with_numbers_grouped_by_file_with_samples()
    {
        var raw = """
            src/a.cs:10:foo bar
            src/a.cs:25:foo bar
            src/b.cs:3:foo bar
            tests/t.cs:7:foo bar
            """;
        var actual = new GrepFilter("grep", DetailLevel.Default).Apply(raw, 0);
        Assert.StartsWith("grep m=4 f=3\n", actual);
        Assert.Contains("top=src/a.cs(2),src/b.cs(1),tests/t.cs(1)\n", actual);
        Assert.Contains("samples:", actual);
        // First sample should reference the most common content "foo bar"
        Assert.Contains("foo bar", actual);
    }

    [Fact]
    public void Top_files_truncated_to_3_with_more_marker_default()
    {
        var raw = string.Join('\n',
            Enumerable.Range(0, 5).SelectMany(f =>
                Enumerable.Range(0, f + 1).Select(_ => $"src/f{f}.cs:1:hit{f}")));
        var actual = new GrepFilter("grep", DetailLevel.Default).Apply(raw, 0);
        Assert.Contains("+2 more files", actual);
        var topLine = actual.Split('\n').First(l => l.StartsWith("top="));
        Assert.Equal(3, topLine["top=".Length..].Split(',').Length);
    }

    [Fact]
    public void More_detail_widens_top_to_6_and_adds_more_section()
    {
        var raw = string.Join('\n',
            Enumerable.Range(0, 10).SelectMany(f =>
                Enumerable.Range(0, f + 1).Select(_ => $"src/f{f}.cs:1:hit{f}")));
        var actual = new GrepFilter("grep", DetailLevel.More).Apply(raw, 0);
        var topLine = actual.Split('\n').First(l => l.StartsWith("top="));
        Assert.Equal(6, topLine["top=".Length..].Split(',').Length);
        Assert.Contains("more=", actual);
    }

    [Fact]
    public void Bare_path_lines_become_paths_section()
    {
        var raw = """
            src/a.cs
            src/b.cs
            tests/t.cs
            """;
        var actual = new GrepFilter("grep", DetailLevel.Default).Apply(raw, 0);
        Assert.StartsWith("grep m=3 f=3\n", actual);
        Assert.Contains("paths:", actual);
        Assert.Contains("  src/a.cs", actual);
    }

    [Fact]
    public void Binary_match_marker_is_counted()
    {
        var raw = """
            Binary file src/data.bin matches
            src/a.cs:1:foo
            """;
        var actual = new GrepFilter("grep", DetailLevel.Default).Apply(raw, 0);
        Assert.Contains("bin=1", actual);
        Assert.Contains("m=1", actual);
        Assert.Contains("f=1", actual);
    }

    [Fact]
    public void Long_sample_content_truncated_to_120_chars()
    {
        var longContent = new string('x', 200);
        // Two files so common-prefix stripping leaves filenames recognisable.
        var raw = $"src/a.cs:1:{longContent}\nsrc/b.cs:2:other";
        var actual = new GrepFilter("grep", DetailLevel.Default).Apply(raw, 0);
        var sampleLine = actual.Split('\n').First(l => l.TrimStart().StartsWith("a.cs"));
        Assert.EndsWith("...", sampleLine);
        // content is capped at 120 chars
        Assert.Contains(new string('x', 120), sampleLine);
        Assert.DoesNotContain(new string('x', 121), sampleLine);
    }
}
