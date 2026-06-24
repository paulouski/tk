using LspSymbolMatch = Tk.Lsp.SymbolMatch;
using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

public class RefsFormatterCandidatesTests
{
    private static LspLocation Loc(string uri, int line, int col) =>
        new(uri, line, col, line, col + 1);

    [Fact]
    public void Single_candidate_shows_count_one()
    {
        var candidates = new LspSymbolMatch[]
        {
            new("Hold", "Wallet", "method", Loc("file:///proj/Wallet.cs", 9, 4)),
        };
        var result = RefsFormatter.FormatCandidates("Hold", candidates);

        Assert.Contains("1 matches", result);
        Assert.Contains("ambiguous", result);
    }

    [Fact]
    public void Multiple_candidates_shows_correct_count()
    {
        var candidates = new LspSymbolMatch[]
        {
            new("Hold", "Wallet", "method", Loc("file:///proj/Wallet.cs", 9, 4)),
            new("Hold", "Account", "method", Loc("file:///proj/Account.cs", 5, 2)),
        };
        var result = RefsFormatter.FormatCandidates("Hold", candidates);

        Assert.Contains("2 matches", result);
    }

    [Fact]
    public void All_candidates_listed()
    {
        var candidates = new LspSymbolMatch[]
        {
            new("Hold", "Wallet", "method", Loc("file:///proj/Wallet.cs", 9, 4)),
            new("Hold", "Account", "method", Loc("file:///proj/Account.cs", 5, 2)),
            new("Hold", "Reserve", "method", Loc("file:///proj/Reserve.cs", 1, 0)),
        };
        var result = RefsFormatter.FormatCandidates("Hold", candidates);

        Assert.Contains("Wallet", result);
        Assert.Contains("Account", result);
        Assert.Contains("Reserve", result);
    }

    [Fact]
    public void Line_and_col_are_one_based()
    {
        // LSP 0-based line 9, char 4 → display 10:5
        var candidates = new LspSymbolMatch[]
        {
            new("Hold", "Wallet", "method", Loc("file:///proj/Wallet.cs", 9, 4)),
        };
        var result = RefsFormatter.FormatCandidates("Hold", candidates);

        Assert.Contains("10:5", result);
    }

    [Fact]
    public void Container_included_when_present()
    {
        var candidates = new LspSymbolMatch[]
        {
            new("Hold", "Wallet", "method", Loc("file:///proj/Wallet.cs", 0, 0)),
        };
        var result = RefsFormatter.FormatCandidates("Hold", candidates);

        Assert.Contains("Wallet.Hold", result);
    }

    [Fact]
    public void Container_omitted_cleanly_when_empty()
    {
        var candidates = new LspSymbolMatch[]
        {
            new("Hold", "", "method", Loc("file:///proj/Wallet.cs", 0, 0)),
        };
        var result = RefsFormatter.FormatCandidates("Hold", candidates);

        // Should not produce ".Hold" with a leading dot
        Assert.DoesNotContain(".Hold", result);
        Assert.Contains("Hold", result);
    }

    [Fact]
    public void Kind_appears_in_output()
    {
        var candidates = new LspSymbolMatch[]
        {
            new("Hold", "Wallet", "method", Loc("file:///proj/Wallet.cs", 0, 0)),
        };
        var result = RefsFormatter.FormatCandidates("Hold", candidates);

        Assert.Contains("method", result);
    }

    [Fact]
    public void Hint_to_rerun_with_position_present()
    {
        var candidates = new LspSymbolMatch[]
        {
            new("Hold", "Wallet", "method", Loc("file:///proj/Wallet.cs", 0, 0)),
        };
        var result = RefsFormatter.FormatCandidates("Hold", candidates);

        Assert.Contains("file:line:col", result);
    }
}
