using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

public class ImplFormatterTests
{
    [Fact]
    public void Empty_locations_returns_zero_count()
    {
        var result = ImplFormatter.Format("IHandler", []);
        Assert.Equal("impl IHandler n=0", result);
    }

    [Fact]
    public void Single_location_shows_correct_counts()
    {
        var locs = new LspLocation[] { new("file:///proj/Foo.cs", 9, 4, 9, 12) };
        var result = ImplFormatter.Format("IHandler", locs);

        Assert.Contains("impl IHandler n=1 f=1", result);
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
        var result = ImplFormatter.Format("IHandler", locs);

        Assert.Contains("n=3", result);
        Assert.Contains("f=2", result);
    }

    [Fact]
    public void Line_numbers_are_one_based()
    {
        var locs = new LspLocation[] { new("file:///proj/Foo.cs", 9, 4, 9, 12) };
        var result = ImplFormatter.Format("IHandler", locs);

        Assert.Contains("10:5", result);
    }

    [Fact]
    public void Multiple_files_sorted_alphabetically()
    {
        var locs = new LspLocation[]
        {
            new("file:///proj/Z.cs", 0, 0, 0, 1),
            new("file:///proj/A.cs", 0, 0, 0, 1),
        };
        var result = ImplFormatter.Format("X", locs);

        var aIdx = result.IndexOf("A.cs", StringComparison.Ordinal);
        var zIdx = result.IndexOf("Z.cs", StringComparison.Ordinal);
        Assert.True(aIdx < zIdx, "A.cs should appear before Z.cs");
    }

    [Fact]
    public void Source_line_preview_shown_when_file_readable()
    {
        var tmpPath = Path.Combine(Path.GetTempPath(), $"ImplFormatterTest_{Guid.NewGuid():N}.cs");
        try
        {
            var content = string.Join(Environment.NewLine,
                "namespace Tmp;",
                "public class FooHandler : IHandler",
                "{",
                "}");
            System.IO.File.WriteAllText(tmpPath, content);

            var fileUri = new Uri(tmpPath).ToString();
            var loc = new LspLocation(fileUri, 1, 0, 1, 10);

            var result = ImplFormatter.Format("IHandler", [loc]);

            Assert.Contains("public class FooHandler : IHandler", result);
        }
        finally
        {
            if (System.IO.File.Exists(tmpPath)) System.IO.File.Delete(tmpPath);
        }
    }

    [Fact]
    public void Missing_file_degrades_to_empty_preview_without_throwing()
    {
        var loc = new LspLocation("file:///nonexistent/Impl.cs", 0, 0, 0, 1);
        var result = ImplFormatter.Format("IHandler", [loc]);

        Assert.Contains("impl IHandler n=1", result);
        Assert.Contains("1:1", result);
    }
}
