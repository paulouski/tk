using Tk.Lsp.Protocol;

namespace Tk.Lsp.RequestHandlers;

/// <summary>
/// Handles <c>def</c> (textDocument/definition) — position-based, or symbol-name-based via
/// workspace/symbol resolution (with an ambiguous-candidates early return when a name
/// matches more than one symbol).
/// </summary>
internal sealed class DefHandler : IRequestHandler
{
    public async Task<DaemonResponse> HandleAsync(LspDaemonContext ctx, DaemonRequest request, CancellationToken ct)
    {
        await ctx.WaitForReadyAsync(ct).ConfigureAwait(false);

        string defPath;
        int defLine;
        int defChar;

        if (request.FilePath is not null)
        {
            defPath = request.FilePath;
            defLine = request.Line;
            defChar = request.Character;
        }
        else if (request.Symbol is not null)
        {
            var matches = await SymbolResolver.ResolveSymbolAsync(ctx.Loop, request.Symbol, ct).ConfigureAwait(false);
            if (matches.Count == 0)
                return new DaemonResponse(false, $"symbol '{request.Symbol}' not found", null);
            if (matches.Count > 1)
                return new DaemonResponse(true, null, null) with { Candidates = matches.ToArray() };

            var loc = matches[0].Location;
            defPath = new Uri(loc.Uri).LocalPath;
            defLine = loc.StartLine;
            defChar = loc.StartChar;
        }
        else
        {
            return new DaemonResponse(false, "def requires a file position or symbol name", null);
        }

        ctx.Log($"def query: {defPath}:{defLine}:{defChar}");
        var locs = await FindDefinitionAsync(ctx.Loop, ctx.EnsureFileOpenAsync, defPath, defLine, defChar, ct).ConfigureAwait(false);
        ctx.Log($"def query done, {locs.Length} locations");
        return new DaemonResponse(true, null, locs);
    }

    /// <summary>
    /// Finds the definition location(s) for the symbol at the given position via
    /// textDocument/definition. Handles null/undefined, single Location, array of
    /// Location, and LocationLink (targetUri / targetSelectionRange / targetRange).
    /// </summary>
    private static async Task<LspLocation[]> FindDefinitionAsync(
        MessageLoop loop, Func<string, string, CancellationToken, Task> ensureFileOpenAsync,
        string filePath, int line, int character, CancellationToken ct)
    {
        var fileUri = new Uri(filePath).ToString();
        await ensureFileOpenAsync(filePath, fileUri, ct).ConfigureAwait(false);

        var defParams = new
        {
            textDocument = new { uri = fileUri },
            position = new { line, character }
        };

        var result = await loop.SendRequestAsync("textDocument/definition", defParams, ct).ConfigureAwait(false);
        return LspResultParser.ParseLocationOrLink(result);
    }
}
