using Tk.Lsp.Protocol;

namespace Tk.Lsp.RequestHandlers;

/// <summary>
/// Handles <c>calls</c> (callHierarchy/outgoingCalls) — position-based, or symbol-name-based
/// via the shared ResolveTargetAsync. Backs <c>tk calls</c>.
/// KNOWN RISK: some Roslyn language-server builds do not implement outgoingCalls and answer
/// with an empty array even for a method that provably calls others — the caller (CallsCommand)
/// surfaces that ambiguity rather than reporting a false "no outgoing calls".
/// </summary>
internal sealed class CallsHandler : IRequestHandler
{
    public async Task<DaemonResponse> HandleAsync(LspDaemonContext ctx, DaemonRequest request, CancellationToken ct)
    {
        await ctx.WaitForReadyAsync(ct).ConfigureAwait(false);

        var (callsTarget, callsCandidates, callsError) = await SymbolResolver.ResolveTargetAsync(
            ctx.Loop, request.FilePath, request.Line, request.Character, request.Symbol, "calls", ct).ConfigureAwait(false);
        if (callsError is not null)
            return new DaemonResponse(false, callsError, null);
        if (callsCandidates is not null)
            return new DaemonResponse(true, null, null) with { Candidates = callsCandidates };

        var calls = callsTarget!.Value;
        ctx.Log($"calls query: {calls.Path}:{calls.Line}:{calls.Character}");
        var callees = await CallHierarchyQuery.FindCallHierarchyAsync(
            ctx.Loop, ctx.EnsureFileOpenAsync, calls.Path, calls.Line, calls.Character,
            "callHierarchy/outgoingCalls", "to", ct).ConfigureAwait(false);
        ctx.Log($"calls query done, {callees.Length} callees");
        return new DaemonResponse(true, null, null) with { Callees = callees };
    }
}
