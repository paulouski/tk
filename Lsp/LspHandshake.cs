using Tk.Lsp.Protocol;

namespace Tk.Lsp;

/// <summary>
/// The LSP initialize handshake: registers the server-initiated request handlers
/// (workspace/configuration, client/registerCapability), sends <c>initialize</c> with the
/// capability object that gates Roslyn project loading, sends <c>initialized</c> only after
/// the initialize result is received (LSP spec order), then waits for
/// <c>workspace/projectInitializationComplete</c> (or a WorkspaceReady $/progress end) before
/// signalling readiness. Extracted from <see cref="LspDaemon"/> as a pure protocol step.
/// </summary>
internal static class LspHandshake
{
    /// <summary>
    /// Runs the initialize handshake on <paramref name="loop"/>, gating readiness on project
    /// load completion. Throws on cancellation/timeout/failure so the caller can let the
    /// exception propagate; transitions the daemon to <c>Failed</c> via <paramref
    /// name="transitionToFailed"/> and reports a fatal startup failure first.
    /// </summary>
    internal static async Task HandshakeAsync(
        MessageLoop loop, string workspaceRoot, CancellationToken ct,
        Action<string> log, Action transitionToReady,
        Action<string> transitionToFailed, Action reportFatalStartupFailure)
    {
        try
        {
            // Register handlers for server-initiated requests BEFORE sending initialize
            loop.RegisterHandler("workspace/configuration", (id, prms) =>
            {
                var count = 0;
                try
                {
                    if (prms.HasValue && prms.Value.TryGetProperty("items", out var items))
                        count = items.GetArrayLength();
                }
                catch { }
                log($"workspace/configuration received ({count} items), answering");
                // Must return one result per requested item (all null = use defaults).
                return Task.FromResult<object?>(new object?[count]);
            });

            loop.RegisterHandler("client/registerCapability", (id, _) =>
            {
                log("client/registerCapability received, answering");
                return Task.FromResult<object?>(null);
            });

            // Send initialize
            log("sending initialize");
            var rootUri = new Uri(workspaceRoot).ToString();
            var initParams = new
            {
                processId = Environment.ProcessId,
                rootUri,
                // Roslyn's --autoLoadProjects reads workspaceFolders, NOT rootUri (deprecated).
                // Without this the server logs "No workspace folders provided ... could not auto
                // load projects", loads nothing, and never emits WorkspaceReady.
                workspaceFolders = new[]
                {
                    new { uri = rootUri, name = Path.GetFileName(workspaceRoot.TrimEnd('/')) }
                },
                // Capabilities + initializationOptions mirror the validated spike. Roslyn
                // gates project loading / progress on these: window.workDoneProgress is
                // required for the WorkspaceReady $/progress; workspace.configuration enables
                // the config handshake; and initializationOptions.hostInfo must be present or
                // the server never starts loading (no progress is ever emitted).
                capabilities = new
                {
                    workspace = new
                    {
                        workspaceFolders = true,
                        configuration = true,
                        didChangeConfiguration = new { dynamicRegistration = true },
                        // Declares support for workspace/didChangeWatchedFiles — Roslyn does
                        // not watch the filesystem itself; it relies on this client capability
                        // (and the notification WatchedFileTracker sends) to learn about .cs/
                        // .csproj/.sln files created, changed, or deleted outside of an
                        // explicit textDocument/didOpen.
                        didChangeWatchedFiles = new { dynamicRegistration = true },
                        symbol = new { dynamicRegistration = false }
                    },
                    textDocument = new
                    {
                        references = new { dynamicRegistration = false },
                        rename = new { dynamicRegistration = false, prepareSupport = true },
                        synchronization = new { dynamicRegistration = false, didSave = false },
                        publishDiagnostics = new { relatedInformation = true },
                        // callHierarchy backs both `tk callers` (incomingCalls) and `tk calls`
                        // (outgoingCalls) — one capability, one prepareCallHierarchy call, two
                        // different follow-up requests.
                        callHierarchy = new { dynamicRegistration = false },
                        // Enables textDocument/diagnostic (LSP 3.17 pull diagnostics) — the
                        // mechanism `tk diag` relies on. See docs/lsp-daemon-architecture.md
                        // for why pull (not publishDiagnostics push) was chosen.
                        diagnostic = new { dynamicRegistration = false },
                        implementation = new { dynamicRegistration = false },
                        // Backs `tk sig` (hover signature/doc lookup).
                        hover = new { dynamicRegistration = false },
                        documentSymbol = new { hierarchicalDocumentSymbolSupport = true },
                        // Backs `tk fix`: request quickfix-kind code actions and, when the
                        // server only returns a partial action (edit resolved lazily), follow
                        // up with codeAction/resolve rather than a workspace/executeCommand
                        // roundtrip we do not implement.
                        codeAction = new
                        {
                            dynamicRegistration = false,
                            codeActionLiteralSupport = new { codeActionKind = new { valueSet = new[] { "quickfix" } } },
                            resolveSupport = new { properties = new[] { "edit" } },
                            isPreferredSupport = true,
                        },
                    },
                    window = new
                    {
                        workDoneProgress = true,
                        showDocument = new { support = true }
                    }
                },
                initializationOptions = new { hostInfo = "tk" }
            };

            // LSP spec order: the client MUST send 'initialized' only AFTER receiving the
            // 'initialize' RESULT. Sending it earlier makes Roslyn skip project loading, so
            // WorkspaceReady never fires. There is no deadlock: the MessageLoop read loop runs
            // independently and answers the server's workspace/configuration request while we
            // await the initialize response.
            await loop.SendRequestAsync("initialize", initParams, ct).ConfigureAwait(false);
            log("initialize response received");

            // Register the readiness watcher BEFORE sending 'initialized'. The
            // projectInitializationComplete notification can arrive before the watcher is
            // registered for a fast-loading project; the loop has no buffering, so a watcher
            // registered afterwards would miss it and we'd hang until the 120s timeout.
            // This server version (Roslyn) signals load completion via the
            // 'workspace/projectInitializationComplete' notification, NOT a $/progress
            // 'WorkspaceReady' end. We accept either for robustness across server versions.
            var readyTask = loop.WaitForNotificationAsync(msg =>
            {
                if (msg.method == "workspace/projectInitializationComplete")
                    return true;

                // Fallback: older/other servers use a WorkspaceReady progress 'end'.
                if (msg.method == "$/progress" && msg.@params is { } p)
                {
                    try
                    {
                        if (p.TryGetProperty("token", out var tok) && tok.GetString() == "WorkspaceReady"
                            && p.TryGetProperty("value", out var val)
                            && val.TryGetProperty("kind", out var kind) && kind.GetString() == "end")
                            return true;
                    }
                    catch { /* ignore malformed progress */ }
                }

                return false;
            }, ct);

            await loop.SendNotificationAsync("initialized", new { }, ct).ConfigureAwait(false);
            log("initialized notification sent");
            log("waiting for workspace/projectInitializationComplete");

            await readyTask.ConfigureAwait(false);
            transitionToReady();
            log("project initialization complete — state → Ready");
        }
        catch (OperationCanceledException ex)
        {
            var reason = ex.CancellationToken.IsCancellationRequested
                ? "workspace-ready-timeout"
                : "cancelled";
            log($"handshake cancelled/timed-out: {reason}");
            transitionToFailed(reason);
            reportFatalStartupFailure();
            throw;
        }
        catch (Exception ex)
        {
            log($"handshake EXCEPTION: {ex}");
            transitionToFailed(ex.Message);
            reportFatalStartupFailure();
            throw;
        }
    }
}
