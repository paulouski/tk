using Tk.Lsp.Protocol;

namespace Tk.Lsp.RequestHandlers;

/// <summary>
/// Handles <c>callers</c> (callHierarchy/incomingCalls) — position-based, or symbol-name-based
/// via workspace/symbol resolution (with an ambiguous-candidates early return). Uses the
/// shared <see cref="CallHierarchyQuery.FindCallHierarchyAsync"/> two-step prepare+incoming.
/// </summary>
internal sealed class CallersHandler : IRequestHandler
{
    public async Task<DaemonResponse> HandleAsync(LspDaemonContext ctx, DaemonRequest request, CancellationToken ct)
    {
        await ctx.WaitForReadyAsync(ct).ConfigureAwait(false);

        string callersPath;
        int callersLine;
        int callersChar;

        if (request.FilePath is not null)
        {
            callersPath = request.FilePath;
            callersLine = request.Line;
            callersChar = request.Character;
        }
        else if (request.Symbol is not null)
        {
            var matches = await SymbolResolver.ResolveSymbolAsync(ctx.Loop, request.Symbol, ct).ConfigureAwait(false);
            if (matches.Count == 0)
                return new DaemonResponse(false, SymbolResolver.NotFoundMessage(request.Symbol, "callers"), null);
            if (matches.Count > 1)
                return new DaemonResponse(true, null, null) with { Candidates = matches.ToArray() };

            var loc = matches[0].Location;
            callersPath = new Uri(loc.Uri).LocalPath;
            callersLine = loc.StartLine;
            callersChar = loc.StartChar;
        }
        else
        {
            return new DaemonResponse(false, "callers requires a file position or symbol name", null);
        }

        ctx.Log($"callers query: {callersPath}:{callersLine}:{callersChar}");
        var callers = await CallHierarchyQuery.FindCallHierarchyAsync(
            ctx.Loop, ctx.EnsureFileOpenAsync, callersPath, callersLine, callersChar,
            "callHierarchy/incomingCalls", "from", ct).ConfigureAwait(false);
        ctx.Log($"callers query done, {callers.Length} callers");
        return new DaemonResponse(true, null, null) with { Callers = callers };
    }
}
