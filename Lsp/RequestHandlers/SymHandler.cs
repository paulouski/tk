namespace Tk.Lsp.RequestHandlers;

/// <summary>
/// Handles <c>sym</c> (workspace/symbol, fuzzy) — a workspace-wide symbol search that keeps
/// every match the server itself considers relevant to the query (<c>exactMatchOnly</c>=false),
/// backing <c>tk sym</c>. Distinct from <see cref="SymbolsHandler"/>, which resolves a name
/// exactly for use by <c>def/refs/callers/impl/rename</c>.
/// </summary>
internal sealed class SymHandler : IRequestHandler
{
    public async Task<DaemonResponse> HandleAsync(LspDaemonContext ctx, DaemonRequest request, CancellationToken ct)
    {
        if (request.Symbol is null)
            return new DaemonResponse(false, $"Unknown method: {request.Method}", null);

        await ctx.WaitForReadyAsync(ct).ConfigureAwait(false);
        ctx.Log($"sym query: {request.Symbol}");
        var symMatches = await SymbolResolver.ResolveSymbolAsync(ctx.Loop, request.Symbol, ct, exactMatchOnly: false).ConfigureAwait(false);
        ctx.Log($"sym query done, {symMatches.Count} matches");
        if (symMatches.Count == 0)
            return new DaemonResponse(false,
                $"symbol '{request.Symbol}' not found in workspace sources — external (NuGet/BCL) " +
                "symbols aren't indexed by workspace/symbol search; 'tk sym' has no position-form fallback",
                null);
        return new DaemonResponse(true, null, null) with { Candidates = symMatches.ToArray() };
    }
}
