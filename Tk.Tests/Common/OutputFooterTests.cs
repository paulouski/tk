using Tk;
using Tk.Common;
using Xunit;

namespace Tk.Tests.Common;

public class OutputFooterTests
{
    [Fact]
    public void Matches_HiddenLinesFooter_when_no_unparsed_or_raw()
    {
        // Byte-compatibility: with unparsedCount=0 and no rawPath, output must be identical to
        // the pre-existing HiddenLinesFooter.Format.
        var legacy = HiddenLinesFooter.Format(100, 60, DetailLevel.Default);
        var migrated = OutputFooter.Format(100, 60, unparsedCount: 0, DetailLevel.Default);
        Assert.Equal(legacy, migrated);
        Assert.Equal("hid=40/100 (--more, --raw)", migrated);
    }

    [Fact]
    public void No_hidden_no_unparsed_no_raw_returns_null()
    {
        Assert.Null(OutputFooter.Format(100, 100, unparsedCount: 0, DetailLevel.Default));
    }

    [Fact]
    public void Unparsed_token_appears_after_hid()
    {
        var result = OutputFooter.Format(100, 60, unparsedCount: 5, DetailLevel.Default);
        Assert.Equal("hid=40/100 unparsed=5 (--more, --raw)", result);
    }

    [Fact]
    public void Unparsed_alone_without_hidden_still_shows_footer()
    {
        var result = OutputFooter.Format(10, 10, unparsedCount: 2, DetailLevel.Default);
        Assert.Equal("unparsed=2 (--more, --raw)", result);
    }

    [Fact]
    public void Raw_path_appears_last_before_hint()
    {
        var result = OutputFooter.Format(100, 60, unparsedCount: 5, DetailLevel.Default, rawPath: "/tmp/raw.log");
        Assert.Equal("hid=40/100 unparsed=5 raw=/tmp/raw.log (--more, --raw)", result);
    }

    [Fact]
    public void Raw_path_alone_still_shows_footer()
    {
        var result = OutputFooter.Format(10, 10, unparsedCount: 0, DetailLevel.Default, rawPath: "/tmp/raw.log");
        Assert.Equal("raw=/tmp/raw.log (--more, --raw)", result);
    }

    [Fact]
    public void More_level_drops_more_hint()
    {
        var result = OutputFooter.Format(100, 60, unparsedCount: 5, DetailLevel.More);
        Assert.Equal("hid=40/100 unparsed=5 (--raw)", result);
    }
}
