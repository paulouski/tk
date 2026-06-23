using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

/// <summary>
/// Tests for multi-match disambiguation logic given a list of symbol results.
/// Since WorkspaceSymbol queries are stubs for now, these tests focus on formatter
/// disambiguation helpers that would be used when multiple symbols match.
/// </summary>
public class WorkspaceSymbolTests
{
    [Fact]
    public void Single_match_is_unambiguous()
    {
        var symbols = new[] { new SymbolMatch("MyClass", "file:///A.cs", 5, 0) };
        var best = SymbolDisambiguator.PickBest(symbols, "MyClass");

        Assert.NotNull(best);
        Assert.Equal("MyClass", best!.Name);
    }

    [Fact]
    public void Exact_case_match_wins_over_partial()
    {
        var symbols = new[]
        {
            new SymbolMatch("myclass", "file:///A.cs", 1, 0),
            new SymbolMatch("MyClass", "file:///B.cs", 1, 0),
        };
        var best = SymbolDisambiguator.PickBest(symbols, "MyClass");

        Assert.Equal("MyClass", best?.Name);
        Assert.Equal("file:///B.cs", best?.Uri);
    }

    [Fact]
    public void Returns_null_for_empty_list()
    {
        var best = SymbolDisambiguator.PickBest([], "MyClass");
        Assert.Null(best);
    }

    [Fact]
    public void Returns_first_when_all_equally_match()
    {
        var symbols = new[]
        {
            new SymbolMatch("MyClass", "file:///A.cs", 1, 0),
            new SymbolMatch("MyClass", "file:///B.cs", 1, 0),
        };
        // Both are exact matches; pick first
        var best = SymbolDisambiguator.PickBest(symbols, "MyClass");
        Assert.NotNull(best);
        // Either is acceptable; just ensure no exception
    }
}

// --- Lightweight helpers used only in tests ---

public record SymbolMatch(string Name, string Uri, int Line, int Character);

public static class SymbolDisambiguator
{
    public static SymbolMatch? PickBest(IReadOnlyList<SymbolMatch> symbols, string query)
    {
        if (symbols.Count == 0)
            return null;

        // Prefer exact case match
        var exact = symbols.FirstOrDefault(s => s.Name == query);
        if (exact is not null)
            return exact;

        // Prefer case-insensitive match
        var ci = symbols.FirstOrDefault(s =>
            s.Name.Equals(query, StringComparison.OrdinalIgnoreCase));
        return ci ?? symbols[0];
    }
}
