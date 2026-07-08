using Tk.Lsp.Protocol;

namespace Tk.Lsp.RequestHandlers;

/// <summary>
/// Handles <c>impl</c> (textDocument/implementation) — position-based, or symbol-name-based
/// via workspace/symbol resolution (with an ambiguous-candidates early return). Same
/// Location/LocationLink result shape as <c>def</c>.
/// </summary>
internal sealed class ImplHandler : IRequestHandler
{
    public async Task<DaemonResponse> HandleAsync(LspDaemonContext ctx, DaemonRequest request, CancellationToken ct)
    {
        await ctx.WaitForReadyAsync(ct).ConfigureAwait(false);

        string implPath;
        int implLine;
        int implChar;

        if (request.FilePath is not null)
        {
            implPath = request.FilePath;
            implLine = request.Line;
            implChar = request.Character;
        }
        else if (request.Symbol is not null)
        {
            var matches = await SymbolResolver.ResolveSymbolAsync(ctx.Loop, request.Symbol, ct).ConfigureAwait(false);
            if (matches.Count == 0)
                return new DaemonResponse(false, $"symbol '{request.Symbol}' not found", null);
            if (matches.Count > 1)
                return new DaemonResponse(true, null, null) with { Candidates = matches.ToArray() };

            var loc = matches[0].Location;
            implPath = new Uri(loc.Uri).LocalPath;
            implLine = loc.StartLine;
            implChar = loc.StartChar;
        }
        else
        {
            return new DaemonResponse(false, "impl requires a file position or symbol name", null);
        }

        ctx.Log($"impl query: {implPath}:{implLine}:{implChar}");
        var implLocs = await FindImplementationsAsync(ctx.Loop, ctx.EnsureFileOpenAsync, implPath, implLine, implChar, ct).ConfigureAwait(false);
        ctx.Log($"impl query done, {implLocs.Length} locations");
        return new DaemonResponse(true, null, implLocs);
    }

    /// <summary>
    /// Finds implementation location(s) for the symbol at the given position via
    /// textDocument/implementation (e.g. classes implementing an interface, or overrides of
    /// an abstract member). Same Location/LocationLink result shape as textDocument/definition.
    /// </summary>
    private static async Task<LspLocation[]> FindImplementationsAsync(
        MessageLoop loop, Func<string, string, CancellationToken, Task> ensureFileOpenAsync,
        string filePath, int line, int character, CancellationToken ct)
    {
        var fileUri = new Uri(filePath).ToString();
        await ensureFileOpenAsync(filePath, fileUri, ct).ConfigureAwait(false);

        var implParams = new
        {
            textDocument = new { uri = fileUri },
            position = new { line, character }
        };

        var result = await loop.SendRequestAsync("textDocument/implementation", implParams, ct).ConfigureAwait(false);
        return LspResultParser.ParseLocationOrLink(result);
    }
}
