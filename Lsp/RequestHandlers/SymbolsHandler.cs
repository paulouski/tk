namespace Tk.Lsp.RequestHandlers;

/// <summary>
/// Handles <c>symbols</c> (workspace/symbol, exact-name) — resolves a symbol name to exact
/// matches (simple name after the last '.'), used by <c>RenameCommand</c>'s conflict checker
/// to enumerate existing symbols with the old/new name. Distinct from <see cref="SymHandler"/>
/// (fuzzy search) — this one keeps only results whose <c>name</c> exactly matches the simple
/// name, matching what <c>def/refs/callers/impl/rename</c> use for name resolution.
/// </summary>
internal sealed class SymbolsHandler : IRequestHandler
{
    public async Task<DaemonResponse> HandleAsync(LspDaemonContext ctx, DaemonRequest request, CancellationToken ct)
    {
        if (request.Symbol is null)
            return new DaemonResponse(false, $"Unknown method: {request.Method}", null);

        await ctx.WaitForReadyAsync(ct).ConfigureAwait(false);
        ctx.Log($"symbols query: {request.Symbol}");
        var matches = await SymbolResolver.ResolveSymbolAsync(ctx.Loop, request.Symbol, ct).ConfigureAwait(false);
        ctx.Log($"symbols query done, {matches.Count} matches");
        return new DaemonResponse(true, null, null) with { Candidates = matches.ToArray() };
    }
}
