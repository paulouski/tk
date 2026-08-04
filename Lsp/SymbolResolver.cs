using System.Text.Json;
using Tk.Lsp.Protocol;

namespace Tk.Lsp;

/// <summary>
/// Shared symbol-name resolution used by the request handlers: resolving a (possibly
/// qualified) symbol name to workspace/symbol matches, and the higher-level
/// "file position, or resolve a symbol name" target resolution used by the
/// position-or-symbol request kinds (sig, calls). Stateless except for the LSP round-trip.
/// </summary>
internal static class SymbolResolver
{
    internal readonly record struct ResolvedTarget(string Path, int Line, int Character);

    /// <summary>
    /// Resolves a symbol name (or qualified name like Namespace.Class.Method) to a list of
    /// matching workspace symbols via workspace/symbol. By default (<paramref
    /// name="exactMatchOnly"/> true — used by def/refs/callers/impl/rename's name resolution)
    /// only results whose 'name' field exactly matches the simple name (the substring after
    /// the last '.') are kept. `tk sym`'s fuzzy workspace-wide search passes false to keep
    /// every match the server itself considers relevant to the query.
    /// </summary>
    internal static async Task<List<SymbolMatch>> ResolveSymbolAsync(
        MessageLoop loop, string symbol, CancellationToken ct, bool exactMatchOnly = true)
    {
        // Use the simple name (after last '.') as the query — servers match on it.
        var simpleName = symbol.Contains('.') ? symbol[(symbol.LastIndexOf('.') + 1)..] : symbol;

        var result = await loop.SendRequestAsync("workspace/symbol", new { query = simpleName }, ct).ConfigureAwait(false);
        return LspResultParser.ParseSymbolMatches(result, exactMatchOnly, simpleName);
    }

    /// <summary>
    /// The error message for "workspace/symbol returned nothing for this name". The server only
    /// indexes symbols declared in the workspace's own sources, so a symbol coming from metadata
    /// (NuGet package or BCL) looks exactly like a symbol that does not exist — an empty result
    /// either way. Name the ambiguity and point at the position form, which skips name resolution
    /// and asks the compiler from a usage site, where metadata symbols do resolve.
    /// </summary>
    internal static string NotFoundMessage(string symbol, string what) =>
        $"symbol '{symbol}' not found in workspace sources — if it is external (NuGet/BCL), " +
        $"use 'tk {what} <file:line:col>' at a usage site";

    /// <summary>
    /// Shared "file position, or resolve a symbol name via workspace/symbol" resolution used
    /// by the newer position-or-symbol request kinds (sig, calls) — the same resolution
    /// def/impl/callers/rename already do inline. Returns exactly one of: a resolved position,
    /// a list of ambiguous candidates (more than one match), or an error message (no match, or
    /// neither a position nor a symbol was given).
    /// </summary>
    internal static async Task<(ResolvedTarget? Position, SymbolMatch[]? Candidates, string? Error)> ResolveTargetAsync(
        MessageLoop loop, string? filePath, int line, int character, string? symbol, string what, CancellationToken ct)
    {
        if (filePath is not null)
            return (new ResolvedTarget(filePath, line, character), null, null);

        if (symbol is not null)
        {
            var matches = await ResolveSymbolAsync(loop, symbol, ct).ConfigureAwait(false);
            if (matches.Count == 0)
                return (null, null, NotFoundMessage(symbol, what));
            if (matches.Count > 1)
                return (null, matches.ToArray(), null);

            var loc = matches[0].Location;
            return (new ResolvedTarget(new Uri(loc.Uri).LocalPath, loc.StartLine, loc.StartChar), null, null);
        }

        return (null, null, $"{what} requires a file position or symbol name");
    }
}
