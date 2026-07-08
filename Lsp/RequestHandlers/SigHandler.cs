using System.Text.Json;
using Tk.Lsp.Protocol;

namespace Tk.Lsp.RequestHandlers;

/// <summary>
/// Handles <c>sig</c> (textDocument/hover) — position-based, or symbol-name-based via the
/// shared ResolveTargetAsync. Returns the raw hover text (markdown, exactly as the server sent
/// it) or null when the server has no hover info for that position. Markdown-fence/noise
/// stripping is a formatter concern (see SigFormatter), not done here.
/// </summary>
internal sealed class SigHandler : IRequestHandler
{
    public async Task<DaemonResponse> HandleAsync(LspDaemonContext ctx, DaemonRequest request, CancellationToken ct)
    {
        await ctx.WaitForReadyAsync(ct).ConfigureAwait(false);

        var (sigTarget, sigCandidates, sigError) = await SymbolResolver.ResolveTargetAsync(
            ctx.Loop, request.FilePath, request.Line, request.Character, request.Symbol, "sig", ct).ConfigureAwait(false);
        if (sigError is not null)
            return new DaemonResponse(false, sigError, null);
        if (sigCandidates is not null)
            return new DaemonResponse(true, null, null) with { Candidates = sigCandidates };

        var sig = sigTarget!.Value;
        ctx.Log($"sig query: {sig.Path}:{sig.Line}:{sig.Character}");
        var hoverText = await FindHoverAsync(ctx.Loop, ctx.EnsureFileOpenAsync, sig.Path, sig.Line, sig.Character, ct).ConfigureAwait(false);
        ctx.Log($"sig query done, hover {(hoverText is null ? "empty" : "present")}");
        var hover = hoverText is null ? null : new HoverResult(new Uri(sig.Path).ToString(), sig.Line, sig.Character, hoverText);
        return new DaemonResponse(true, null, null) with { Hover = hover };
    }

    /// <summary>
    /// Finds hover contents (signature/doc-comment) for the symbol at the given position via
    /// textDocument/hover. Returns the raw hover text or null when the server has no hover
    /// info for that position.
    /// </summary>
    private static async Task<string?> FindHoverAsync(
        MessageLoop loop, Func<string, string, CancellationToken, Task> ensureFileOpenAsync,
        string filePath, int line, int character, CancellationToken ct)
    {
        var fileUri = new Uri(filePath).ToString();
        await ensureFileOpenAsync(filePath, fileUri, ct).ConfigureAwait(false);

        var hoverParams = new
        {
            textDocument = new { uri = fileUri },
            position = new { line, character }
        };

        var result = await loop.SendRequestAsync("textDocument/hover", hoverParams, ct).ConfigureAwait(false);
        if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
            return null;

        return result.TryGetProperty("contents", out var contents) ? LspResultParser.ParseHoverText(contents) : null;
    }
}
