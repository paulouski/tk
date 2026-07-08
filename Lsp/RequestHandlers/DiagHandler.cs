using Tk.Lsp.Protocol;

namespace Tk.Lsp.RequestHandlers;

/// <summary>
/// Handles <c>diag</c> (textDocument/diagnostic, LSP 3.17 pull diagnostics) for one or more
/// files — <c>request.Paths</c> carries the files (a project/directory scope) and each is
/// pulled in its own round trip. Returns per-file diagnostics in <c>DiagnosticsByFile</c>.
/// </summary>
internal sealed class DiagHandler : IRequestHandler
{
    public async Task<DaemonResponse> HandleAsync(LspDaemonContext ctx, DaemonRequest request, CancellationToken ct)
    {
        if (request.Paths is not { Length: > 0 })
            return new DaemonResponse(false, $"Unknown method: {request.Method}", null);

        await ctx.WaitForReadyAsync(ct).ConfigureAwait(false);
        ctx.Log($"diag query: {request.Paths.Length} file(s)");

        var byFile = new List<FileDiagnostics>();
        foreach (var path in request.Paths)
        {
            var diags = await FindFileDiagnosticsAsync(ctx.Loop, ctx.EnsureFileOpenAsync, path, ct).ConfigureAwait(false);
            byFile.Add(new FileDiagnostics(new Uri(path).ToString(), diags));
        }

        ctx.Log($"diag query done, {byFile.Sum(f => f.Diagnostics.Length)} diagnostics across {byFile.Count} file(s)");
        return new DaemonResponse(true, null, null) with { DiagnosticsByFile = byFile.ToArray() };
    }

    /// <summary>
    /// Pulls diagnostics for a single file via textDocument/diagnostic. No previousResultId is
    /// sent, so the server always answers with a full report (never "unchanged"). Also used by
    /// <see cref="FixHandler"/> to enumerate fixable diagnostics.
    /// </summary>
    internal static async Task<LspDiagnostic[]> FindFileDiagnosticsAsync(
        MessageLoop loop, Func<string, string, CancellationToken, Task> ensureFileOpenAsync,
        string filePath, CancellationToken ct)
    {
        var fileUri = new Uri(filePath).ToString();
        await ensureFileOpenAsync(filePath, fileUri, ct).ConfigureAwait(false);

        var diagParams = new { textDocument = new { uri = fileUri } };
        var result = await loop.SendRequestAsync("textDocument/diagnostic", diagParams, ct).ConfigureAwait(false);
        return LspResultParser.ParseDiagnostics(result);
    }
}
