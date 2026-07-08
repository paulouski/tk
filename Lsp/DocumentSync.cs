using Tk.Lsp.Protocol;

namespace Tk.Lsp;

/// <summary>
/// Owns the textDocument/didOpen / didClose resync state for files the daemon queries:
/// the <c>_openDocs</c> map (URI → open-state), the serializing <c>_openLock</c>, and the
/// <see cref="EnsureFileOpenAsync"/> flow that opens a file the first time it's queried and
/// resyncs it (via didClose+didOpen) when it was edited on disk since the last open. Roslyn
/// requires a document to be open before it answers position-based queries.
///
/// The pure freshness decision (<see cref="LspDaemon.DecideSyncAction"/> / <see
/// cref="LspDaemon.SyncAction"/>) stays on <see cref="LspDaemon"/> for back-compat with
/// <c>DocSyncTests</c>; this class consumes it.
/// </summary>
internal sealed class DocumentSync
{
    // Files already sent via textDocument/didOpen (required before queries; the server
    // throws "Unexpected null" in FindAllReferencesHandler for an unopened document), keyed
    // by URI, with the LSP document version and the source mtime as of the last open/sync —
    // used by DecideSyncAction to detect edits made outside this process (e.g. by the agent,
    // with no `dotnet build` in between) and resync via didChange before querying.
    private readonly Dictionary<string, OpenDocState> _openDocs = new(StringComparer.Ordinal);

    // Serializes the whole open/resync decision+notification (not just the dictionary
    // mutation) so two concurrent requests for the same URI can't race a didOpen against a
    // didChange, or send two didChange notifications with the same version. Held across
    // await, hence SemaphoreSlim rather than a plain lock.
    private readonly SemaphoreSlim _openLock = new(1, 1);

    private readonly Action<string> _log;

    internal DocumentSync(Action<string> log)
    {
        _log = log;
    }

    internal readonly record struct OpenDocState(int Version, DateTime Mtime);

    /// <summary>
    /// Sends textDocument/didOpen for a file the first time it's queried; on later calls,
    /// resyncs it via <see cref="LspDaemon.DecideSyncAction"/> against the file's current
    /// mtime — a didChange (full-document replace) if it was edited on disk since the last
    /// open/sync (e.g. by the agent, with no `dotnet build` in between), or a didClose if it
    /// was deleted. Roslyn requires the document to be open before it will answer
    /// position-based queries; otherwise it faults with "Unexpected null".
    /// </summary>
    internal async Task EnsureFileOpenAsync(MessageLoop loop, string filePath, string fileUri, CancellationToken ct)
    {
        await _openLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var fileExists = File.Exists(filePath);
            var currentMtime = fileExists ? File.GetLastWriteTimeUtc(filePath) : default;

            if (!_openDocs.TryGetValue(fileUri, out var state))
            {
                if (!fileExists) return;

                string text;
                try { text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false); }
                catch { return; }

                await loop.SendNotificationAsync("textDocument/didOpen", new
                {
                    textDocument = new { uri = fileUri, languageId = "csharp", version = 1, text }
                }, ct).ConfigureAwait(false);
                _log($"didOpen {fileUri}");
                _openDocs[fileUri] = new OpenDocState(1, currentMtime);

                // Give the server a moment to register the document before querying.
                await Task.Delay(300, ct).ConfigureAwait(false);
                return;
            }

            switch (LspDaemon.DecideSyncAction(state.Mtime, fileExists, currentMtime))
            {
                case LspDaemon.SyncAction.Close:
                    await loop.SendNotificationAsync("textDocument/didClose", new
                    {
                        textDocument = new { uri = fileUri }
                    }, ct).ConfigureAwait(false);
                    _openDocs.Remove(fileUri);
                    _log($"didClose (file missing) {fileUri}");
                    break;

                case LspDaemon.SyncAction.Change:
                    string newText;
                    try { newText = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false); }
                    catch { return; }

                    // A rangeless (whole-document) textDocument/didChange — the LSP-spec shape
                    // for full-document sync — crashes this server version: its
                    // DidChangeHandler.GetUpdatedSourceText dereferences the (absent) range
                    // unconditionally (NullReferenceException in
                    // ProtocolConversions.RangeToLinePositionSpan), which takes the whole
                    // Roslyn child process down (SIGABRT), zombie-ing the daemon. didClose +
                    // didOpen achieves the same "resync to current content" outcome without
                    // going anywhere near that code path, at the cost of one extra
                    // notification — resync is already the cold path (only fires when the
                    // file changed since it was last opened), so this isn't perf-sensitive.
                    await loop.SendNotificationAsync("textDocument/didClose", new
                    {
                        textDocument = new { uri = fileUri }
                    }, ct).ConfigureAwait(false);
                    await loop.SendNotificationAsync("textDocument/didOpen", new
                    {
                        textDocument = new { uri = fileUri, languageId = "csharp", version = 1, text = newText }
                    }, ct).ConfigureAwait(false);
                    _openDocs[fileUri] = new OpenDocState(1, currentMtime);
                    _log($"didClose+didOpen (stale) {fileUri}");

                    // Give the server a moment to reprocess before querying.
                    await Task.Delay(300, ct).ConfigureAwait(false);
                    break;

                case LspDaemon.SyncAction.None:
                default:
                    break;
            }
        }
        finally
        {
            _openLock.Release();
        }
    }
}
