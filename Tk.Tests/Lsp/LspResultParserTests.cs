using System.Text.Json;
using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

/// <summary>
/// Unit tests for <see cref="LspResultParser.ParseDocumentSymbols"/>, the parser that turns a
/// <c>textDocument/documentSymbol</c> LSP response into the
/// <see cref="DocumentSymbolInfo"/> tree carried back over the daemon socket for <c>tk view</c>'s
/// LSP-backed outline. Covers: null/undefined/empty inputs, single symbol, kind→name mapping,
/// nested children, detail passthrough, malformed-item dropping. Real-world responses are
/// arrays of <c>DocumentSymbol</c> objects with <c>name</c>/<c>kind</c>/<c>range</c> plus
/// optional <c>selectionRange</c>/<c>detail</c>/<c>children</c>.
/// </summary>
public class LspResultParserTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Null_result_returns_empty()
    {
        var result = LspResultParser.ParseDocumentSymbols(Parse("null"));
        Assert.Empty(result);
    }

    [Fact]
    public void Undefined_result_returns_empty()
    {
        // Undefined can't be serialised directly, but the parser must also handle a JSON
        // value of `undefined` shape — the closest JSON equivalent is no value (which is
        // null), covered above. We additionally verify the empty-array case since some
        // servers return `[]` for files with no symbols.
        var result = LspResultParser.ParseDocumentSymbols(Parse("[]"));
        Assert.Empty(result);
    }

    [Fact]
    public void Non_array_result_returns_empty()
    {
        // Defensive: a server returning an object instead of an array must not crash the
        // parser (we'd rather see an empty outline than a 500).
        var result = LspResultParser.ParseDocumentSymbols(Parse("""{"unexpected": "shape"}"""));
        Assert.Empty(result);
    }

    [Fact]
    public void Single_class_symbol_maps_kind_and_range()
    {
        var json = """
        [
          {
            "name": "BigClass",
            "kind": 5,
            "range": {"start": {"line": 0, "character": 0}, "end": {"line": 50, "character": 1}},
            "selectionRange": {"start": {"line": 0, "character": 13}, "end": {"line": 0, "character": 21}}
          }
        ]
        """;

        var result = LspResultParser.ParseDocumentSymbols(Parse(json));

        var sym = Assert.Single(result);
        Assert.Equal("BigClass", sym.Name);
        Assert.Equal("class", sym.Kind);
        Assert.Equal(0, sym.StartLine);
        Assert.Equal(0, sym.StartChar);
        Assert.Equal(50, sym.EndLine);
        Assert.Equal(1, sym.EndChar);
        Assert.Null(sym.Detail);
        Assert.Null(sym.Children);
    }

    [Fact]
    public void Method_with_detail_string_is_preserved()
    {
        var json = """
        [
          {
            "name": "RunAsync",
            "kind": 6,
            "range": {"start": {"line": 10, "character": 4}, "end": {"line": 30, "character": 5}},
            "detail": "RunAsync(CancellationToken cancellationToken)"
          }
        ]
        """;

        var result = LspResultParser.ParseDocumentSymbols(Parse(json));

        var sym = Assert.Single(result);
        Assert.Equal("RunAsync", sym.Name);
        Assert.Equal("method", sym.Kind);
        Assert.Equal("RunAsync(CancellationToken cancellationToken)", sym.Detail);
    }

    [Fact]
    public void Empty_detail_string_is_normalised_to_null()
    {
        // Some servers emit `"detail": ""` for symbols without a signature; the renderer
        // checks for null to decide whether to render `(detail)` so we normalise to null.
        var json = """
        [
          {"name": "X", "kind": 5, "range": {"start": {"line": 0, "character": 0}, "end": {"line": 0, "character": 0}}, "detail": ""}
        ]
        """;

        var result = LspResultParser.ParseDocumentSymbols(Parse(json));

        Assert.Null(Assert.Single(result).Detail);
    }

    [Fact]
    public void Hierarchical_children_are_preserved()
    {
        // Class with two methods nested under it. The class range covers the whole body;
        // the method ranges are the body of each method. This is the typical Roslyn shape
        // and the one the outline renderer relies on for indentation.
        var json = """
        [
          {
            "name": "LspDaemon",
            "kind": 5,
            "range": {"start": {"line": 26, "character": 0}, "end": {"line": 1473, "character": 1}},
            "children": [
              {
                "name": "RunAsync",
                "kind": 6,
                "range": {"start": {"line": 75, "character": 4}, "end": {"line": 182, "character": 5}},
                "detail": "RunAsync(CancellationToken cancellationToken)"
              },
              {
                "name": "HandleMessageAsync",
                "kind": 6,
                "range": {"start": {"line": 184, "character": 4}, "end": {"line": 259, "character": 5}}
              }
            ]
          }
        ]
        """;

        var result = LspResultParser.ParseDocumentSymbols(Parse(json));

        var cls = Assert.Single(result);
        Assert.Equal("LspDaemon", cls.Name);
        Assert.Equal(26, cls.StartLine);
        Assert.Equal(1473, cls.EndLine);
        Assert.NotNull(cls.Children);
        Assert.Equal(2, cls.Children!.Length);

        var runAsync = cls.Children[0];
        Assert.Equal("RunAsync", runAsync.Name);
        Assert.Equal("method", runAsync.Kind);
        Assert.Equal(75, runAsync.StartLine);
        Assert.Equal(182, runAsync.EndLine);
        Assert.Equal("RunAsync(CancellationToken cancellationToken)", runAsync.Detail);

        var handleMessage = cls.Children[1];
        Assert.Equal("HandleMessageAsync", handleMessage.Name);
        Assert.Null(handleMessage.Detail);
    }

    [Fact]
    public void Unknown_kind_falls_back_to_symbol()
    {
        // The SymbolKind enum has more values than our map covers (File=1, Module=2,
        // Namespace=3, etc.). Unknown kinds must not crash — they map to "symbol".
        var json = """
        [
          {"name": "SomeNamespace", "kind": 3, "range": {"start": {"line": 0, "character": 0}, "end": {"line": 5, "character": 0}}}
        ]
        """;

        var result = LspResultParser.ParseDocumentSymbols(Parse(json));

        Assert.Equal("symbol", Assert.Single(result).Kind);
    }

    [Fact]
    public void Item_missing_name_is_dropped()
    {
        // A server reply with a partial item (e.g. only `kind` and `range` but no `name`)
        // must not crash the parser — the whole-file outline is best-effort and one bad
        // entry shouldn't take down the rest of the response.
        var json = """
        [
          {"kind": 6, "range": {"start": {"line": 0, "character": 0}, "end": {"line": 1, "character": 0}}},
          {"name": "Keep", "kind": 6, "range": {"start": {"line": 5, "character": 0}, "end": {"line": 6, "character": 0}}}
        ]
        """;

        var result = LspResultParser.ParseDocumentSymbols(Parse(json));

        var kept = Assert.Single(result);
        Assert.Equal("Keep", kept.Name);
    }

    [Fact]
    public void Empty_children_array_does_not_become_a_null_node()
    {
        // Roslyn sometimes returns `"children": []` for a class with no nested symbols;
        // the renderer should treat that the same as no `children` key (null).
        var json = """
        [
          {"name": "Empty", "kind": 5, "range": {"start": {"line": 0, "character": 0}, "end": {"line": 1, "character": 0}}, "children": []}
        ]
        """;

        var result = LspResultParser.ParseDocumentSymbols(Parse(json));

        var sym = Assert.Single(result);
        Assert.Null(sym.Children);
    }
}
