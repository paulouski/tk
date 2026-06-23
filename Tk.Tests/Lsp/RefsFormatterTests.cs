using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

public class RefsFormatterTests
{
    [Fact]
    public void Empty_locations_returns_zero_counts()
    {
        var result = RefsFormatter.Format("MyMethod", []);
        Assert.Equal("refs MyMethod n=0 f=0", result);
    }

    [Fact]
    public void Single_location_shows_correct_counts()
    {
        var locs = new LspLocation[] { new("file:///proj/Foo.cs", 9, 4, 9, 12) };
        var result = RefsFormatter.Format("MyMethod", locs);

        Assert.Contains("refs MyMethod n=1 f=1", result);
    }

    [Fact]
    public void Locations_grouped_by_file()
    {
        var locs = new LspLocation[]
        {
            new("file:///proj/A.cs", 0, 0, 0, 5),
            new("file:///proj/B.cs", 2, 1, 2, 6),
            new("file:///proj/A.cs", 10, 0, 10, 5),
        };
        var result = RefsFormatter.Format("Sym", locs);

        // n=3 total, f=2 files
        Assert.Contains("n=3", result);
        Assert.Contains("f=2", result);
    }

    [Fact]
    public void Line_numbers_are_one_based()
    {
        // LSP 0-based line 9 → display 10
        var locs = new LspLocation[] { new("file:///proj/Foo.cs", 9, 4, 9, 12) };
        var result = RefsFormatter.Format("MyMethod", locs);

        Assert.Contains("10:5", result); // line 9->10, char 4->5
    }

    [Fact]
    public void Multiple_files_sorted_alphabetically()
    {
        var locs = new LspLocation[]
        {
            new("file:///proj/Z.cs", 0, 0, 0, 1),
            new("file:///proj/A.cs", 0, 0, 0, 1),
        };
        var result = RefsFormatter.Format("X", locs);

        var aIdx = result.IndexOf("A.cs", StringComparison.Ordinal);
        var zIdx = result.IndexOf("Z.cs", StringComparison.Ordinal);
        Assert.True(aIdx < zIdx, "A.cs should appear before Z.cs");
    }

    [Fact]
    public void File_count_appears_in_header()
    {
        var locs = new LspLocation[]
        {
            new("file:///proj/A.cs", 0, 0, 0, 1),
            new("file:///proj/B.cs", 0, 0, 0, 1),
            new("file:///proj/C.cs", 0, 0, 0, 1),
        };
        var result = RefsFormatter.Format("Thing", locs);

        Assert.Contains("f=3", result);
    }
}
