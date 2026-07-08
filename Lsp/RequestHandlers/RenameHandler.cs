using Tk.Lsp.Protocol;

namespace Tk.Lsp.RequestHandlers;

/// <summary>
/// Handles <c>rename</c> (textDocument/rename) — a position-based rename. Returns the
/// server-computed <c>WorkspaceEdit</c> (per file: TextEdit[]) so the client can apply
/// them. Backs <c>tk rename</c>.
/// </summary>
internal sealed class RenameHandler : IRequestHandler
{
    public async Task<DaemonResponse> HandleAsync(LspDaemonContext ctx, DaemonRequest request, CancellationToken ct)
    {
        if (request.FilePath is null || request.NewName is null)
            return new DaemonResponse(false, $"Unknown method: {request.Method}", null);

        await ctx.WaitForReadyAsync(ct).ConfigureAwait(false);
        ctx.Log($"rename: {request.FilePath}:{request.Line}:{request.Character} -> {request.NewName}");

        var edits = await RenameAsync(ctx.Loop, ctx.EnsureFileOpenAsync, request.FilePath, request.Line, request.Character, request.NewName, ct).ConfigureAwait(false);
        var n = edits.Sum(f => f.Edits.Length);
        ctx.Log($"rename done, {n} edits in {edits.Length} files");
        return new DaemonResponse(true, null, null) with { Edits = edits };
    }

    private static async Task<FileEdits[]> RenameAsync(
        MessageLoop loop, Func<string, string, CancellationToken, Task> ensureFileOpenAsync,
        string filePath, int line, int character, string newName, CancellationToken ct)
    {
        var fileUri = new Uri(filePath).ToString();
        await ensureFileOpenAsync(filePath, fileUri, ct).ConfigureAwait(false);

        var renameParams = new
        {
            textDocument = new { uri = fileUri },
            position = new { line, character },
            newName
        };

        var result = await loop.SendRequestAsync("textDocument/rename", renameParams, ct).ConfigureAwait(false);
        return LspResultParser.ParseFileEdits(result);
    }
}
