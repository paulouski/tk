using Tk;
using Tk.Common;
using Xunit;

namespace Tk.Tests.Common;

public class HiddenLinesFooterTests
{
    [Fact]
    public void Reduction_returns_footer_with_both_hints()
    {
        var result = HiddenLinesFooter.Format(100, 60, DetailLevel.Default);
        Assert.Equal("hid=40/100 (--more, --raw)", result);
    }

    [Fact]
    public void No_reduction_returns_null()
    {
        var result = HiddenLinesFooter.Format(100, 100, DetailLevel.Default);
        Assert.Null(result);
    }

    [Fact]
    public void Shown_exceeds_original_returns_null()
    {
        var result = HiddenLinesFooter.Format(50, 60, DetailLevel.Default);
        Assert.Null(result);
    }

    [Fact]
    public void Zero_original_lines_returns_null()
    {
        var result = HiddenLinesFooter.Format(0, 0, DetailLevel.Default);
        Assert.Null(result);
    }

    [Fact]
    public void More_level_drops_more_hint()
    {
        var result = HiddenLinesFooter.Format(100, 60, DetailLevel.More);
        Assert.Equal("hid=40/100 (--raw)", result);
    }

    [Fact]
    public void More_level_no_reduction_returns_null()
    {
        var result = HiddenLinesFooter.Format(100, 100, DetailLevel.More);
        Assert.Null(result);
    }

    [Fact]
    public void CountLines_trailing_newline_not_inflated()
    {
        // "a\nb\n" should count as 2 lines, not 3
        Assert.Equal(2, HiddenLinesFooter.CountLines("a\nb\n"));
    }

    [Fact]
    public void CountLines_no_trailing_newline()
    {
        Assert.Equal(2, HiddenLinesFooter.CountLines("a\nb"));
    }

    [Fact]
    public void CountLines_empty_string()
    {
        // "" splits to [""], but [""] has one trailing-empty element -> 0
        Assert.Equal(0, HiddenLinesFooter.CountLines(""));
    }

    [Fact]
    public void CountLines_single_newline()
    {
        // "\n" splits to ["", ""] -> trailing empty -> 1 line
        Assert.Equal(1, HiddenLinesFooter.CountLines("\n"));
    }

    [Fact]
    public void Passthrough_equal_counts_returns_null()
    {
        var text = "line1\nline2\nline3\n";
        var original = HiddenLinesFooter.CountLines(text);
        var shown = HiddenLinesFooter.CountLines(text);
        Assert.Null(HiddenLinesFooter.Format(original, shown, DetailLevel.Default));
    }
}
