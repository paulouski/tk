using Tk.Lsp.Protocol;

namespace Tk.Lsp.RequestHandlers;

/// <summary>
/// Handles <c>outline</c> (textDocument/documentSymbol) — whole-document semantic outline used
/// by <c>tk view</c> for large <c>.cs</c> files. The request is always the full document
/// (no position is consulted; Line/Character on the request are ignored), so the LSP round
/// trip returns one hierarchical tree of symbols, parsed via
/// <see cref="LspResultParser.ParseDocumentSymbols"/>.
/// </summary>
internal sealed class DocumentSymbolHandler : IRequestHandler
{
    public async Task<DaemonResponse> HandleAsync(LspDaemonContext ctx, DaemonRequest request, CancellationToken ct)
    {
        if (request.FilePath is null)
            return new DaemonResponse(false, "outline requires a file path", null);

        await ctx.WaitForReadyAsync(ct).ConfigureAwait(false);

        var filePath = request.FilePath;
        var fileUri = new Uri(filePath).ToString();

        ctx.Log($"outline query: {filePath}");
        await ctx.EnsureFileOpenAsync(filePath, fileUri, ct).ConfigureAwait(false);

        var outlineParams = new
        {
            textDocument = new { uri = fileUri }
        };

        var result = await ctx.Loop.SendRequestAsync("textDocument/documentSymbol", outlineParams, ct).ConfigureAwait(false);
        var symbols = LspResultParser.ParseDocumentSymbols(result);
        ctx.Log($"outline query done, {symbols.Length} top-level symbols");
        return new DaemonResponse(true, null, null) with { Outline = symbols };
    }
}
