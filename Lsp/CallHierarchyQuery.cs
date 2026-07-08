using System.Text.Json;
using Tk.Lsp.Protocol;

namespace Tk.Lsp;

/// <summary>
/// Shared two-step call-hierarchy query (textDocument/prepareCallHierarchy then
/// callHierarchy/incomingCalls or callHierarchy/outgoingCalls) used by both
/// <c>CallersHandler</c> (incoming) and <c>CallsHandler</c> (outgoing). Owns the
/// prepare-step item extraction; the result-array parse lives in
/// <see cref="LspResultParser.ParseCallHierarchyResult"/>.
/// </summary>
internal static class CallHierarchyQuery
{
    /// <summary>
    /// Prepares the hierarchy item at <c>(filePath, line, character)</c>, then sends
    /// <paramref name="callMethod"/> ("callHierarchy/incomingCalls" or
    /// "callHierarchy/outgoingCalls") and parses the result. <paramref name="itemField"/> is
    /// "from" for incoming / "to" for outgoing.
    /// </summary>
    internal static async Task<CallerInfo[]> FindCallHierarchyAsync(
        MessageLoop loop, Func<string, string, CancellationToken, Task> ensureFileOpenAsync,
        string filePath, int line, int character,
        string callMethod, string itemField, CancellationToken ct)
    {
        var fileUri = new Uri(filePath).ToString();
        await ensureFileOpenAsync(filePath, fileUri, ct).ConfigureAwait(false);

        var prepareParams = new
        {
            textDocument = new { uri = fileUri },
            position = new { line, character }
        };

        var prepareResult = await loop.SendRequestAsync("textDocument/prepareCallHierarchy", prepareParams, ct).ConfigureAwait(false);

        if (prepareResult.ValueKind == JsonValueKind.Null || prepareResult.ValueKind == JsonValueKind.Undefined)
            return [];

        // Result is an array; take the first item
        JsonElement itemEl;
        if (prepareResult.ValueKind == JsonValueKind.Array)
        {
            if (prepareResult.GetArrayLength() == 0) return [];
            itemEl = prepareResult[0].Clone();
        }
        else
        {
            // Some servers may return a single object (non-standard) — handle gracefully
            itemEl = prepareResult.Clone();
        }

        var callParams = new { item = itemEl };
        var callResult = await loop.SendRequestAsync(callMethod, callParams, ct).ConfigureAwait(false);

        return LspResultParser.ParseCallHierarchyResult(callResult, itemField, fileUri);
    }
}
