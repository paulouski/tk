using Tk.Lsp.Protocol;

namespace Tk.Lsp.RequestHandlers;

/// <summary>
/// Handles <c>refs</c> (textDocument/references) — both the position-based and the
/// symbol-name-based forms (the two <c>request.Method == "refs"</c> branches): when a file
/// position is given, queries references directly; when only a symbol name is given, resolves
/// it via workspace/symbol first and either reports ambiguous candidates or queries references
/// at the resolved position.
/// </summary>
internal sealed class RefsHandler : IRequestHandler
{
    public async Task<DaemonResponse> HandleAsync(LspDaemonContext ctx, DaemonRequest request, CancellationToken ct)
    {
        if (request.FilePath is not null)
        {
            await ctx.WaitForReadyAsync(ct).ConfigureAwait(false);
            ctx.Log($"refs query: {request.FilePath}:{request.Line}:{request.Character}");

            var locs = await FindReferencesAsync(ctx.Loop, ctx.EnsureFileOpenAsync, request.FilePath, request.Line, request.Character, ct).ConfigureAwait(false);
            ctx.Log($"refs query done, {locs.Length} locations");
            return new DaemonResponse(true, null, locs);
        }

        if (request.Symbol is not null)
        {
            await ctx.WaitForReadyAsync(ct).ConfigureAwait(false);
            ctx.Log($"refs by symbol: {request.Symbol}");

            var matches = await SymbolResolver.ResolveSymbolAsync(ctx.Loop, request.Symbol, ct).ConfigureAwait(false);
            if (matches.Count == 0)
                return new DaemonResponse(false, $"symbol '{request.Symbol}' not found", null);

            if (matches.Count == 1)
            {
                var loc = matches[0].Location;
                var path = new Uri(loc.Uri).LocalPath;
                var locs = await FindReferencesAsync(ctx.Loop, ctx.EnsureFileOpenAsync, path, loc.StartLine, loc.StartChar, ct).ConfigureAwait(false);
                ctx.Log($"refs by symbol done, {locs.Length} locations");
                return new DaemonResponse(true, null, locs);
            }

            ctx.Log($"refs by symbol ambiguous, {matches.Count} candidates");
            return new DaemonResponse(true, null, null) with { Candidates = matches.ToArray() };
        }

        // Neither a position nor a symbol — mirrors the original fallthrough to the
        // "Unknown method" else branch for a malformed refs request.
        return new DaemonResponse(false, $"Unknown method: {request.Method}", null);
    }

    private static async Task<LspLocation[]> FindReferencesAsync(
        MessageLoop loop, Func<string, string, CancellationToken, Task> ensureFileOpenAsync,
        string filePath, int line, int character, CancellationToken ct)
    {
        var fileUri = new Uri(filePath).ToString();
        await ensureFileOpenAsync(filePath, fileUri, ct).ConfigureAwait(false);

        var refsParams = new
        {
            textDocument = new { uri = fileUri },
            position = new { line, character },
            context = new { includeDeclaration = true }
        };

        var result = await loop.SendRequestAsync("textDocument/references", refsParams, ct).ConfigureAwait(false);
        return LspResultParser.ParseLocations(result);
    }
}
