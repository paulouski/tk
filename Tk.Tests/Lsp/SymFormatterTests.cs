using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

public class SymFormatterTests
{
    // Fully qualified: this test's namespace (Tk.Tests.Lsp) also has its own local
    // SymbolMatch type (see WorkspaceSymbolTests.cs) which would otherwise shadow the real
    // Tk.Lsp.SymbolMatch used by SymFormatter.
    private static Tk.Lsp.SymbolMatch Match(string name, string container, string kind, string uri, int line) =>
        new(name, container, kind, new LspLocation(uri, line, 0, line, name.Length));

    [Fact]
    public void Empty_matches_returns_zero_count()
    {
        var result = SymFormatter.Format("Foo", []);
        Assert.Equal("sym Foo n=0", result);
    }

    [Fact]
    public void Single_match_shows_correct_counts()
    {
        var matches = new[] { Match("FooHandler", "", "class", "file:///proj/Foo.cs", 4) };
        var result = SymFormatter.Format("Foo", matches);

        Assert.Contains("sym Foo n=1 f=1", result);
    }

    [Fact]
    public void Matches_grouped_by_file()
    {
        var matches = new[]
        {
            Match("A", "", "class", "file:///proj/A.cs", 0),
            Match("B", "", "class", "file:///proj/B.cs", 0),
            Match("A2", "", "class", "file:///proj/A.cs", 10),
        };
        var result = SymFormatter.Format("A", matches);

        Assert.Contains("n=3", result);
        Assert.Contains("f=2", result);
    }

    [Fact]
    public void Line_numbers_are_one_based()
    {
        var matches = new[] { Match("Foo", "", "method", "file:///proj/Foo.cs", 9) };
        var result = SymFormatter.Format("Foo", matches);

        Assert.Contains("10:1", result);
    }

    [Fact]
    public void Container_name_prefixes_label_when_present()
    {
        var matches = new[] { Match("Handle", "OrderService", "method", "file:///proj/OrderService.cs", 0) };
        var result = SymFormatter.Format("Handle", matches);

        Assert.Contains("OrderService.Handle", result);
    }

    [Fact]
    public void Cap_hides_remainder_and_discloses_more_hint()
    {
        var matches = Enumerable.Range(0, 5)
            .Select(i => Match($"Sym{i}", "", "class", $"file:///proj/F{i}.cs", 0))
            .ToArray();

        var result = SymFormatter.Format("Sym", matches, cap: 2);

        Assert.Contains("n=5", result);
        Assert.Contains("(top 2, --more)", result);
        Assert.Contains("Sym0", result);
        Assert.Contains("Sym1", result);
        Assert.DoesNotContain("Sym4", result);
    }

    [Fact]
    public void Full_result_within_cap_does_not_disclose_more_hint()
    {
        var matches = new[] { Match("Foo", "", "class", "file:///proj/Foo.cs", 0) };
        var result = SymFormatter.Format("Foo", matches, cap: 50);

        Assert.DoesNotContain("--more", result);
    }
}
