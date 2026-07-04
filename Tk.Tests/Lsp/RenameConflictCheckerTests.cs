using LspSymbolMatch = Tk.Lsp.SymbolMatch;
using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

public class RenameConflictCheckerTests
{
    // ── ExtractIdentifier ───────────────────────────────────────────────────

    [Fact]
    public void ExtractIdentifier_returns_full_word_when_position_at_start()
    {
        var result = RenameConflictChecker.ExtractIdentifier("interface IInvoiceRepository", 10);
        Assert.Equal("IInvoiceRepository", result);
    }

    [Fact]
    public void ExtractIdentifier_returns_full_word_when_position_mid_word()
    {
        var result = RenameConflictChecker.ExtractIdentifier("interface IInvoiceRepository", 15);
        Assert.Equal("IInvoiceRepository", result);
    }

    [Fact]
    public void ExtractIdentifier_handles_underscore_and_digits()
    {
        var result = RenameConflictChecker.ExtractIdentifier("var _field2Name = 1;", 5);
        Assert.Equal("_field2Name", result);
    }

    [Fact]
    public void ExtractIdentifier_returns_empty_on_whitespace()
    {
        var result = RenameConflictChecker.ExtractIdentifier("interface IInvoiceRepository", 9);
        Assert.Equal("", result);
    }

    [Fact]
    public void ExtractIdentifier_returns_empty_for_out_of_range()
    {
        Assert.Equal("", RenameConflictChecker.ExtractIdentifier("abc", -1));
        Assert.Equal("", RenameConflictChecker.ExtractIdentifier("abc", 10));
        Assert.Equal("", RenameConflictChecker.ExtractIdentifier("", 0));
    }

    // ── FindDeclarationMatch ────────────────────────────────────────────────

    [Fact]
    public void FindDeclarationMatch_prefers_exact_uri_and_line_match()
    {
        var candidates = new[]
        {
            new LspSymbolMatch("Foo", "NsA", "class", new LspLocation("file:///A.cs", 3, 0, 3, 3)),
            new LspSymbolMatch("Foo", "NsB", "class", new LspLocation("file:///B.cs", 10, 0, 10, 3)),
        };

        var result = RenameConflictChecker.FindDeclarationMatch(candidates, "file:///B.cs", 10);

        Assert.NotNull(result);
        Assert.Equal("NsB", result!.ContainerName);
    }

    [Fact]
    public void FindDeclarationMatch_falls_back_to_sole_same_file_match()
    {
        var candidates = new[]
        {
            // Declaration line differs slightly from a multi-line signature's reported line.
            new LspSymbolMatch("Foo", "NsA", "interface", new LspLocation("file:///A.cs", 5, 0, 5, 3)),
        };

        var result = RenameConflictChecker.FindDeclarationMatch(candidates, "file:///A.cs", 7);

        Assert.NotNull(result);
        Assert.Equal("NsA", result!.ContainerName);
    }

    [Fact]
    public void FindDeclarationMatch_returns_null_when_no_candidate_shares_uri()
    {
        var candidates = new[]
        {
            new LspSymbolMatch("Foo", "NsA", "class", new LspLocation("file:///A.cs", 3, 0, 3, 3)),
        };

        var result = RenameConflictChecker.FindDeclarationMatch(candidates, "file:///Other.cs", 3);

        Assert.Null(result);
    }

    [Fact]
    public void FindDeclarationMatch_returns_null_when_multiple_candidates_share_uri_ambiguously()
    {
        var candidates = new[]
        {
            new LspSymbolMatch("Add", "TypeA", "method", new LspLocation("file:///A.cs", 3, 0, 3, 3)),
            new LspSymbolMatch("Add", "TypeB", "method", new LspLocation("file:///A.cs", 20, 0, 20, 3)),
        };

        var result = RenameConflictChecker.FindDeclarationMatch(candidates, "file:///A.cs", 7);

        Assert.Null(result);
    }

    // ── FindConflict ────────────────────────────────────────────────────────

    [Fact]
    public void FindConflict_detects_same_container_collision()
    {
        var newNameCandidates = new[]
        {
            new LspSymbolMatch("ICommentRepository", "Acme.Data", "interface",
                new LspLocation("file:///Existing.cs", 8, 0, 8, 10)),
        };

        var conflict = RenameConflictChecker.FindConflict("Acme.Data", newNameCandidates);

        Assert.NotNull(conflict);
        Assert.Equal("Acme.Data", conflict!.ContainerName);
    }

    [Fact]
    public void FindConflict_ignores_matches_in_a_different_container()
    {
        var newNameCandidates = new[]
        {
            new LspSymbolMatch("ICommentRepository", "Other.Namespace", "interface",
                new LspLocation("file:///Elsewhere.cs", 8, 0, 8, 10)),
        };

        var conflict = RenameConflictChecker.FindConflict("Acme.Data", newNameCandidates);

        Assert.Null(conflict);
    }

    [Fact]
    public void FindConflict_returns_null_for_no_candidates()
    {
        var conflict = RenameConflictChecker.FindConflict("Acme.Data", []);
        Assert.Null(conflict);
    }
}
