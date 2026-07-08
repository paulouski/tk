using System.Text.Json;
using Tk.Lsp.Protocol;

namespace Tk.Lsp;

/// <summary>
/// Pure, stateless JSON <c>JsonElement</c>→record parsers for the LSP response shapes this
/// daemon consumes, plus the small static maps (symbol-kind / diagnostic-severity) shared by
/// the request handlers. No I/O, no state — every method is a pure projection of its input.
/// </summary>
internal static class LspResultParser
{
    /// <summary>
    /// Parses a textDocument/references result (array of Location) into <see cref="LspLocation"/>s.
    /// Null/undefined → empty.
    /// </summary>
    internal static LspLocation[] ParseLocations(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
            return [];

        var locations = new List<LspLocation>();
        foreach (var item in result.EnumerateArray())
        {
            var uri = item.GetProperty("uri").GetString() ?? "";
            var range = item.GetProperty("range");
            locations.Add(ParseRangeToLocation(uri, range));
        }

        return [.. locations];
    }

    /// <summary>
    /// Shared result parsing for textDocument/definition and textDocument/implementation:
    /// both return null/undefined, a single Location, an array of Location, or an array of
    /// LocationLink (targetUri / targetSelectionRange / targetRange).
    /// </summary>
    internal static LspLocation[] ParseLocationOrLink(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
            return [];

        var locations = new List<LspLocation>();

        // Result may be a single object or an array; normalise to iteration.
        IEnumerable<JsonElement> elements = result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray()
            : [result];

        foreach (var el in elements)
        {
            // Determine uri: Location has "uri"; LocationLink has "targetUri".
            string? uri = null;
            if (el.TryGetProperty("uri", out var uriProp))
                uri = uriProp.GetString();
            else if (el.TryGetProperty("targetUri", out var targetUriProp))
                uri = targetUriProp.GetString();

            if (string.IsNullOrEmpty(uri))
                continue;

            // Determine range: Location has "range"; LocationLink has "targetSelectionRange" then "targetRange".
            JsonElement range = default;
            if (el.TryGetProperty("range", out var rangeProp))
                range = rangeProp;
            else if (el.TryGetProperty("targetSelectionRange", out var tsr))
                range = tsr;
            else if (el.TryGetProperty("targetRange", out var tr))
                range = tr;

            if (range.ValueKind == JsonValueKind.Undefined)
                continue;

            locations.Add(ParseRangeToLocation(uri, range));
        }

        return [.. locations];
    }

    /// <summary>
    /// Parses an LSP <c>range</c> (<c>{ start: {line,character}, end: {line,character} }</c>)
    /// into an <see cref="LspLocation"/> for the given URI. Missing fields default to 0 / start.
    /// </summary>
    internal static LspLocation ParseRangeToLocation(string uri, JsonElement range)
    {
        var start = range.TryGetProperty("start", out var sp) ? sp : default;
        var end = range.TryGetProperty("end", out var ep) ? ep : default;
        var sl = start.ValueKind != JsonValueKind.Undefined && start.TryGetProperty("line", out var slp) ? slp.GetInt32() : 0;
        var sc = start.ValueKind != JsonValueKind.Undefined && start.TryGetProperty("character", out var scp) ? scp.GetInt32() : 0;
        var el = end.ValueKind != JsonValueKind.Undefined && end.TryGetProperty("line", out var elp) ? elp.GetInt32() : sl;
        var ec = end.ValueKind != JsonValueKind.Undefined && end.TryGetProperty("character", out var ecp) ? ecp.GetInt32() : sc;
        return new LspLocation(uri, sl, sc, el, ec);
    }

    /// <summary>
    /// Parses an array of LSP <c>TextEdit</c> objects into <see cref="RenameTextEdit"/>s.
    /// </summary>
    internal static RenameTextEdit[] ParseTextEdits(JsonElement editsArray)
    {
        var list = new List<RenameTextEdit>();
        foreach (var edit in editsArray.EnumerateArray())
        {
            var range = edit.GetProperty("range");
            var start = range.GetProperty("start");
            var end = range.GetProperty("end");
            var newText = edit.GetProperty("newText").GetString() ?? "";
            list.Add(new RenameTextEdit(
                start.GetProperty("line").GetInt32(),
                start.GetProperty("character").GetInt32(),
                end.GetProperty("line").GetInt32(),
                end.GetProperty("character").GetInt32(),
                newText));
        }
        return [.. list];
    }

    /// <summary>
    /// Parses a textDocument/rename WorkspaceEdit result (either <c>changes</c> uri→TextEdit[]
    /// map, or <c>documentChanges</c> array of <c>{textDocument:{uri}, edits:[]}</c>) into
    /// <see cref="FileEdits"/> per file. Null/undefined → empty.
    /// </summary>
    internal static FileEdits[] ParseFileEdits(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
            return [];

        // Parse result.changes (object map: uri -> TextEdit[])
        if (result.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object)
        {
            var fileEditsList = new List<FileEdits>();
            foreach (var prop in changes.EnumerateObject())
            {
                var edits = ParseTextEdits(prop.Value);
                fileEditsList.Add(new FileEdits(prop.Name, edits));
            }
            return [.. fileEditsList];
        }

        // Parse result.documentChanges (array: [{textDocument:{uri}, edits:[]}])
        if (result.TryGetProperty("documentChanges", out var docChanges) && docChanges.ValueKind == JsonValueKind.Array)
        {
            var fileEditsList = new List<FileEdits>();
            foreach (var item in docChanges.EnumerateArray())
            {
                var uri = item.GetProperty("textDocument").GetProperty("uri").GetString() ?? "";
                var edits = ParseTextEdits(item.GetProperty("edits"));
                fileEditsList.Add(new FileEdits(uri, edits));
            }
            return [.. fileEditsList];
        }

        return [];
    }

    /// <summary>
    /// Extracts the <c>TextEdit</c>s targeting <paramref name="fileUri"/> out of a
    /// WorkspaceEdit (<c>changes</c> uri→map and/or <c>documentChanges</c> array), handling
    /// both shapes — same two shapes <see cref="ParseFileEdits"/> parses. Edits for any other
    /// file are dropped: <c>tk fix</c> is single-file by design.
    /// </summary>
    internal static List<RenameTextEdit> ParseFileEditsForUri(JsonElement edit, string fileUri)
    {
        var result = new List<RenameTextEdit>();

        if (edit.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in changes.EnumerateObject())
            {
                if (!string.Equals(prop.Name, fileUri, StringComparison.Ordinal)) continue;
                result.AddRange(ParseTextEdits(prop.Value));
            }
        }

        if (edit.TryGetProperty("documentChanges", out var docChanges) && docChanges.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in docChanges.EnumerateArray())
            {
                if (!item.TryGetProperty("textDocument", out var td)) continue;
                if (!td.TryGetProperty("uri", out var uriProp)) continue;
                if (!string.Equals(uriProp.GetString(), fileUri, StringComparison.Ordinal)) continue;
                if (!item.TryGetProperty("edits", out var editsProp)) continue;
                result.AddRange(ParseTextEdits(editsProp));
            }
        }

        return result;
    }

    /// <summary>
    /// Parses a callHierarchy/incomingCalls (or outgoingCalls) result array into
    /// <see cref="CallerInfo"/>s. <paramref name="itemField"/> is "from" for incoming / "to"
    /// for outgoing; the call-site (fromRanges) URI is stamped per the LSP spec: the source
    /// file at <paramref name="fileUri"/> for outgoing calls, but each caller's own
    /// <c>target.uri</c> for incoming calls.
    /// </summary>
    internal static CallerInfo[] ParseCallHierarchyResult(
        JsonElement callResult, string itemField, string fileUri)
    {
        if (callResult.ValueKind == JsonValueKind.Null || callResult.ValueKind == JsonValueKind.Undefined)
            return [];

        var results = new List<CallerInfo>();
        foreach (var call in callResult.EnumerateArray())
        {
            if (!call.TryGetProperty(itemField, out var target)) continue;

            var targetName = target.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
            var targetKind = target.TryGetProperty("kind", out var kp) ? kp.GetInt32() : 0;
            var targetDetail = target.TryGetProperty("detail", out var dp) ? dp.GetString() ?? "" : "";

            // selectionRange preferred over range for the symbol name position
            JsonElement selRange;
            if (!target.TryGetProperty("selectionRange", out selRange))
                if (!target.TryGetProperty("range", out selRange))
                    continue;

            if (!target.TryGetProperty("uri", out var targetUriProp)) continue;
            var targetUri = targetUriProp.GetString() ?? "";

            var targetLoc = ParseRangeToLocation(targetUri, selRange);

            // Call-site URI differs by direction per the LSP spec: for incoming calls the
            // ranges live inside the caller's own file (targetUri), but for outgoing calls
            // they live inside the original file at fileUri — the item we started
            // prepareCallHierarchy on, not the "to" item's file.
            var callSiteUri = itemField == "to" ? fileUri : targetUri;

            // Parse fromRanges (the actual call sites)
            var callSites = new List<LspLocation>();
            if (call.TryGetProperty("fromRanges", out var fromRanges) && fromRanges.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in fromRanges.EnumerateArray())
                    callSites.Add(ParseRangeToLocation(callSiteUri, r));
            }

            results.Add(new CallerInfo(targetName, targetDetail, SymbolKindName(targetKind), targetLoc, [.. callSites]));
        }

        return [.. results];
    }

    /// <summary>
    /// Parses a textDocument/diagnostic DocumentDiagnosticReport result (kind: "full" with
    /// items) into <see cref="LspDiagnostic"/>s. Non-"full" or missing items → empty.
    /// </summary>
    internal static LspDiagnostic[] ParseDiagnostics(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
            return [];

        // DocumentDiagnosticReport: { kind: "full"|"unchanged", resultId?, items?: Diagnostic[] }
        if (!result.TryGetProperty("kind", out var kindProp) || kindProp.GetString() != "full")
            return [];

        if (!result.TryGetProperty("items", out var itemsProp) || itemsProp.ValueKind != JsonValueKind.Array)
            return [];

        var diagnostics = new List<LspDiagnostic>();
        foreach (var item in itemsProp.EnumerateArray())
        {
            if (!item.TryGetProperty("range", out var range)) continue;
            if (!range.TryGetProperty("start", out var start)) continue;
            if (!range.TryGetProperty("end", out var end)) continue;

            var severity = item.TryGetProperty("severity", out var sevProp) ? sevProp.GetInt32() : 1;
            string? code = null;
            if (item.TryGetProperty("code", out var codeProp))
            {
                code = codeProp.ValueKind == JsonValueKind.String
                    ? codeProp.GetString()
                    : codeProp.ValueKind is JsonValueKind.Number
                        ? codeProp.GetRawText()
                        : null;
            }
            var message = item.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "" : "";

            diagnostics.Add(new LspDiagnostic(
                start.TryGetProperty("line", out var sl) ? sl.GetInt32() : 0,
                start.TryGetProperty("character", out var sc) ? sc.GetInt32() : 0,
                end.TryGetProperty("line", out var el) ? el.GetInt32() : 0,
                end.TryGetProperty("character", out var ec) ? ec.GetInt32() : 0,
                DiagnosticSeverityName(severity),
                code,
                message));
        }

        return [.. diagnostics];
    }

    /// <summary>
    /// Extracts plain text out of an LSP hover "contents" value, which may be a bare string, a
    /// MarkupContent/MarkedString object ({ value } or { language, value }), or an array of
    /// either (joined with a blank line between entries).
    /// </summary>
    internal static string? ParseHoverText(JsonElement contents)
    {
        switch (contents.ValueKind)
        {
            case JsonValueKind.String:
                return contents.GetString();
            case JsonValueKind.Object:
                return contents.TryGetProperty("value", out var valueProp) ? valueProp.GetString() : null;
            case JsonValueKind.Array:
                var parts = new List<string>();
                foreach (var item in contents.EnumerateArray())
                {
                    var text = ParseHoverText(item);
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add(text);
                }
                return parts.Count == 0 ? null : string.Join("\n\n", parts);
            default:
                return null;
        }
    }

    /// <summary>
    /// Parses a workspace/symbol result (array of SymbolInformation) into <see cref="SymbolMatch"/>es,
    /// keeping only results whose <c>name</c> exactly matches <paramref name="simpleName"/> when
    /// <paramref name="exactMatchOnly"/> is true. Items lacking a usable location/range are dropped.
    /// </summary>
    internal static List<SymbolMatch> ParseSymbolMatches(JsonElement result, bool exactMatchOnly, string simpleName)
    {
        if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
            return [];

        var matches = new List<SymbolMatch>();
        foreach (var item in result.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var nameProp)) continue;
            var name = nameProp.GetString() ?? "";
            if (exactMatchOnly && name != simpleName) continue;

            // location is required; skip items without it or without a range
            if (!item.TryGetProperty("location", out var locationEl)) continue;
            if (!locationEl.TryGetProperty("uri", out var uriProp)) continue;
            if (!locationEl.TryGetProperty("range", out var rangeProp)) continue;

            var uri = uriProp.GetString() ?? "";
            if (!rangeProp.TryGetProperty("start", out var startProp)) continue;
            if (!rangeProp.TryGetProperty("end", out var endProp)) continue;

            var startLine = startProp.TryGetProperty("line", out var sl) ? sl.GetInt32() : 0;
            var startChar = startProp.TryGetProperty("character", out var sc) ? sc.GetInt32() : 0;
            var endLine = endProp.TryGetProperty("line", out var el) ? el.GetInt32() : startLine;
            var endChar = endProp.TryGetProperty("character", out var ec) ? ec.GetInt32() : startChar;

            var kind = item.TryGetProperty("kind", out var kindProp) ? kindProp.GetInt32() : 0;
            var container = item.TryGetProperty("containerName", out var cnProp) ? cnProp.GetString() ?? "" : "";

            matches.Add(new SymbolMatch(name, container, SymbolKindName(kind), new LspLocation(uri, startLine, startChar, endLine, endChar)));
        }

        return matches;
    }

    /// <summary>
    /// Parses a textDocument/documentSymbol result (an array of <c>DocumentSymbol</c>) into a
    /// flat-input/hierarchical-output tree of <see cref="DocumentSymbolInfo"/>. Each input
    /// symbol carries <c>name</c>, <c>kind</c> (int), <c>range</c> (full symbol extent), and
    /// optional <c>selectionRange</c> / <c>detail</c> / <c>children</c>. The full <c>range</c>
    /// is used for the outline (it's the body extent, e.g. class line..closing-brace line);
    /// <c>selectionRange</c> would shrink the displayed span to just the name position. Null
    /// /undefined result → empty array. Items missing <c>name</c>/<c>kind</c>/<c>range</c> are
    /// dropped so a partial server reply can't crash the renderer.
    /// </summary>
    internal static DocumentSymbolInfo[] ParseDocumentSymbols(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
            return [];

        if (result.ValueKind != JsonValueKind.Array)
            return [];

        var symbols = new List<DocumentSymbolInfo>();
        foreach (var item in result.EnumerateArray())
        {
            var parsed = ParseOneDocumentSymbol(item);
            if (parsed is not null) symbols.Add(parsed);
        }

        return [.. symbols];
    }

    private static DocumentSymbolInfo? ParseOneDocumentSymbol(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (!item.TryGetProperty("name", out var nameProp)) return null;
        if (!item.TryGetProperty("kind", out var kindProp)) return null;
        if (!item.TryGetProperty("range", out var rangeProp)) return null;

        var start = rangeProp.TryGetProperty("start", out var sp) ? sp : default;
        var end = rangeProp.TryGetProperty("end", out var ep) ? ep : default;
        var sl = start.ValueKind != JsonValueKind.Undefined && start.TryGetProperty("line", out var slp) ? slp.GetInt32() : 0;
        var sc = start.ValueKind != JsonValueKind.Undefined && start.TryGetProperty("character", out var scp) ? scp.GetInt32() : 0;
        var el = end.ValueKind != JsonValueKind.Undefined && end.TryGetProperty("line", out var elp) ? elp.GetInt32() : sl;
        var ec = end.ValueKind != JsonValueKind.Undefined && end.TryGetProperty("character", out var ecp) ? ecp.GetInt32() : sc;

        var name = nameProp.GetString() ?? "";
        var kind = SymbolKindName(kindProp.GetInt32());
        string? detail = item.TryGetProperty("detail", out var detailProp) ? detailProp.GetString() : null;
        if (string.IsNullOrEmpty(detail)) detail = null;

        DocumentSymbolInfo[]? children = null;
        if (item.TryGetProperty("children", out var childrenProp) && childrenProp.ValueKind == JsonValueKind.Array)
        {
            var childList = new List<DocumentSymbolInfo>();
            foreach (var child in childrenProp.EnumerateArray())
            {
                var parsed = ParseOneDocumentSymbol(child);
                if (parsed is not null) childList.Add(parsed);
            }
            if (childList.Count > 0) children = [.. childList];
        }

        return new DocumentSymbolInfo(name, kind, sl, sc, el, ec, detail, children);
    }

    internal static string SymbolKindName(int kind) => kind switch
    {
        5 => "class",
        6 => "method",
        7 => "property",
        8 => "field",
        9 => "constructor",
        10 => "enum",
        11 => "interface",
        12 => "function",
        13 => "variable",
        22 => "enumMember",
        23 => "struct",
        26 => "typeParameter",
        _ => "symbol",
    };

    internal static string DiagnosticSeverityName(int severity) => severity switch
    {
        1 => "error",
        2 => "warning",
        3 => "info",
        4 => "hint",
        _ => "info",
    };

    internal static int DiagnosticSeverityNumber(string severity) => severity switch
    {
        "error" => 1,
        "warning" => 2,
        "info" => 3,
        "hint" => 4,
        _ => 3,
    };
}
