using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tk.Lsp.Protocol;

namespace Tk.Lsp;

/// <summary>
/// Daemon state as it progresses through startup.
/// </summary>
public enum DaemonState
{
    Loading,
    Ready,
    Failed
}

/// <summary>
/// The LSP daemon process. Launches the language server, performs the handshake,
/// then serves textDocument/references requests over a unix socket.
///
/// Architecture (socket-first):
///   1. Socket is bound and listening IMMEDIATELY on startup, before any server interaction.
///   2. Server launch + handshake run in a background task.
///   3. Clients that connect while state=Loading are held until Ready or Failed.
/// </summary>
public sealed class LspDaemon
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(120);

    private readonly string _workspaceRoot;
    private readonly ILanguageBackend _backend;
    private readonly string _logPath;

    // State machine
    private volatile DaemonState _state = DaemonState.Loading;
    private volatile string? _failReason;
    private readonly TaskCompletionSource<bool> _readyTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Set for the duration of RunAsync. A client's "stop" request calls
    // DaemonHost.RequestShutdown() on it, which unwinds the host's accept loop and runs its
    // cleanup (kills the backend child, removes the socket/pid files).
    private DaemonHost? _host;

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

    private readonly record struct OpenDocState(int Version, DateTime Mtime);

    public DaemonState State => _state;

    public LspDaemon(string workspaceRoot, ILanguageBackend backend)
    {
        _workspaceRoot = workspaceRoot;
        _backend = backend;
        _logPath = DaemonSocket.GetLogPath(workspaceRoot);
    }

    // For testing: allow injecting a ready/failed state externally
    internal void SetReady() => TransitionToReady();
    internal void SetFailed(string reason) => TransitionToFailed(reason);

    // Expose the ready task for tests
    internal Task<bool> ReadyTask => _readyTcs.Task;

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        Log("daemon starting");

        var serverPath = _backend.ResolveServer();
        if (serverPath is null)
        {
            var msg = $"LSP server not found. {_backend.InstallHint}";
            Log($"FATAL: {msg}");
            throw new InvalidOperationException(msg);
        }

        // Diagnostic: point the server's own extension logs to a dir next to our daemon log.
        var extLogDir = Path.Combine(Path.GetDirectoryName(_logPath)!, "serverlogs");
        Directory.CreateDirectory(extLogDir);

        var host = new DaemonHost();
        _host = host;
        MessageLoop? loop = null;
        CancellationTokenSource? handshakeCts = null;
        Task? handshakeTask = null;

        var options = new DaemonHost.HostOptions(
            WorkspaceRoot: _workspaceRoot,
            Log: Log,
            StartBackend: () =>
            {
                var args = _backend.GetLaunchArgs(serverPath)
                    .Concat(["--extensionLogDirectory", extLogDir])
                    .ToArray();
                var executable = args[0];
                var launchArgs = string.Join(" ", args[1..].Select(a => $"\"{a}\""));
                return new System.Diagnostics.ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = launchArgs,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = _workspaceRoot,
                };
            },
            OnBackendStarted: (process, backendCt) =>
            {
                // Handshake runs on a background task with its own timeout so client
                // connections (queued on the socket since it's already listening) are never
                // blocked behind it.
                handshakeCts = new CancellationTokenSource(HandshakeTimeout);
                var combinedHandshake = CancellationTokenSource.CreateLinkedTokenSource(backendCt, handshakeCts.Token);

                loop = new MessageLoop(process.StandardOutput.BaseStream, process.StandardInput.BaseStream)
                {
                    Trace = m => Log($"<< {m}")
                };

                handshakeTask = Task.Run(
                    () => HandshakeAsync(loop, _workspaceRoot, combinedHandshake.Token, host.ReportFatalStartupFailure),
                    combinedHandshake.Token);

                return Task.CompletedTask;
            },
            HandleClient: (client, clientCt) => HandleClientAsync(client, loop!, clientCt),
            OnBackendExited: code =>
            {
                // Detect an early server crash (SIGABRT on startup): turn a silent 120s
                // handshake hang into an immediate Failed with the exit code, and let the
                // daemon exit right away instead of idling until the timeout.
                if (_state == DaemonState.Loading)
                {
                    TransitionToFailed($"server-exited code={code}");
                    try { handshakeCts?.Cancel(); } catch { }
                    host.ReportFatalStartupFailure();
                }
            });

        var outcome = await host.RunAsync(options, ct).ConfigureAwait(false);
        if (outcome == DaemonStartOutcome.AlreadyRunningElsewhere)
        {
            Log("another daemon already owns this workspace; standing down");
            return;
        }

        // Await handshake to propagate exceptions
        if (handshakeTask is not null)
        {
            try { await handshakeTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"handshake task exception: {ex}"); }
        }

        if (loop is not null)
            await loop.DisposeAsync().ConfigureAwait(false);
        handshakeCts?.Dispose();
    }

    private async Task HandshakeAsync(MessageLoop loop, string workspaceRoot, CancellationToken ct, Action reportFatalStartupFailure)
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
                Log($"workspace/configuration received ({count} items), answering");
                // Must return one result per requested item (all null = use defaults).
                return Task.FromResult<object?>(new object?[count]);
            });

            loop.RegisterHandler("client/registerCapability", (id, _) =>
            {
                Log("client/registerCapability received, answering");
                return Task.FromResult<object?>(null);
            });

            // Send initialize
            Log("sending initialize");
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
            Log("initialize response received");

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
            Log("initialized notification sent");
            Log("waiting for workspace/projectInitializationComplete");

            await readyTask.ConfigureAwait(false);
            Log("project initialization complete — state → Ready");
            TransitionToReady();
        }
        catch (OperationCanceledException ex)
        {
            var reason = ex.CancellationToken.IsCancellationRequested
                ? "workspace-ready-timeout"
                : "cancelled";
            Log($"handshake cancelled/timed-out: {reason}");
            TransitionToFailed(reason);
            reportFatalStartupFailure();
            throw;
        }
        catch (Exception ex)
        {
            Log($"handshake EXCEPTION: {ex}");
            TransitionToFailed(ex.Message);
            reportFatalStartupFailure();
            throw;
        }
    }

    private void TransitionToReady()
    {
        _state = DaemonState.Ready;
        _readyTcs.TrySetResult(true);
    }

    private void TransitionToFailed(string reason)
    {
        _failReason = reason;
        _state = DaemonState.Failed;
        _readyTcs.TrySetException(new InvalidOperationException($"Daemon failed: {reason}"));
    }

    private async Task WaitForReadyAsync(CancellationToken ct)
    {
        if (_state == DaemonState.Ready) return;
        if (_state == DaemonState.Failed)
            throw new InvalidOperationException($"Daemon failed: {_failReason}");

        // Use WaitAsync so each caller's cancellation is independent and
        // does not poison the shared _readyTcs for other callers.
        await _readyTcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<LspLocation[]> FindReferencesAsync(
        MessageLoop loop, string filePath, int line, int character, CancellationToken ct)
    {
        var fileUri = new Uri(filePath).ToString();
        await EnsureFileOpenAsync(loop, filePath, fileUri, ct).ConfigureAwait(false);

        var refsParams = new
        {
            textDocument = new { uri = fileUri },
            position = new { line, character },
            context = new { includeDeclaration = true }
        };

        var result = await loop.SendRequestAsync("textDocument/references", refsParams, ct).ConfigureAwait(false);

        if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
            return [];

        var locations = new List<LspLocation>();
        foreach (var item in result.EnumerateArray())
        {
            var uri = item.GetProperty("uri").GetString() ?? "";
            var range = item.GetProperty("range");
            var start = range.GetProperty("start");
            var end = range.GetProperty("end");
            locations.Add(new LspLocation(
                uri,
                start.GetProperty("line").GetInt32(),
                start.GetProperty("character").GetInt32(),
                end.GetProperty("line").GetInt32(),
                end.GetProperty("character").GetInt32()));
        }

        return [.. locations];
    }

    private async Task<FileEdits[]> RenameAsync(
        MessageLoop loop, string filePath, int line, int character, string newName, CancellationToken ct)
    {
        var fileUri = new Uri(filePath).ToString();
        await EnsureFileOpenAsync(loop, filePath, fileUri, ct).ConfigureAwait(false);

        var renameParams = new
        {
            textDocument = new { uri = fileUri },
            position = new { line, character },
            newName
        };

        var result = await loop.SendRequestAsync("textDocument/rename", renameParams, ct).ConfigureAwait(false);

        if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
            return [];

        // Parse result.changes (object map: uri -> TextEdit[])
        if (result.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object)
        {
            var fileEditsList = new List<FileEdits>();
            foreach (var prop in changes.EnumerateObject())
            {
                var edits = ParseTextEdits(prop.Value);
                fileEditsList.Add(new FileEdits(prop.Name, edits));
            }
            return [.. fileEditsList];
        }

        // Parse result.documentChanges (array: [{textDocument:{uri}, edits:[]}])
        if (result.TryGetProperty("documentChanges", out var docChanges) && docChanges.ValueKind == JsonValueKind.Array)
        {
            var fileEditsList = new List<FileEdits>();
            foreach (var item in docChanges.EnumerateArray())
            {
                var uri = item.GetProperty("textDocument").GetProperty("uri").GetString() ?? "";
                var edits = ParseTextEdits(item.GetProperty("edits"));
                fileEditsList.Add(new FileEdits(uri, edits));
            }
            return [.. fileEditsList];
        }

        return [];
    }

    /// <summary>
    /// Resolves a symbol name (or qualified name like Namespace.Class.Method) to a list of
    /// matching workspace symbols via workspace/symbol. By default (<paramref
    /// name="exactMatchOnly"/> true — used by def/refs/callers/impl/rename's name resolution)
    /// only results whose 'name' field exactly matches the simple name (the substring after
    /// the last '.') are kept. `tk sym`'s fuzzy workspace-wide search passes false to keep
    /// every match the server itself considers relevant to the query.
    /// </summary>
    private async Task<List<SymbolMatch>> ResolveSymbolAsync(
        MessageLoop loop, string symbol, CancellationToken ct, bool exactMatchOnly = true)
    {
        // Use the simple name (after last '.') as the query — servers match on it.
        var simpleName = symbol.Contains('.') ? symbol[(symbol.LastIndexOf('.') + 1)..] : symbol;

        var result = await loop.SendRequestAsync("workspace/symbol", new { query = simpleName }, ct).ConfigureAwait(false);

        if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
            return [];

        var matches = new List<SymbolMatch>();
        foreach (var item in result.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var nameProp)) continue;
            var name = nameProp.GetString() ?? "";
            if (exactMatchOnly && name != simpleName) continue;

            // location is required; skip items without it or without a range
            if (!item.TryGetProperty("location", out var locationEl)) continue;
            if (!locationEl.TryGetProperty("uri", out var uriProp)) continue;
            if (!locationEl.TryGetProperty("range", out var rangeProp)) continue;

            var uri = uriProp.GetString() ?? "";
            if (!rangeProp.TryGetProperty("start", out var startProp)) continue;
            if (!rangeProp.TryGetProperty("end", out var endProp)) continue;

            var startLine = startProp.TryGetProperty("line", out var sl) ? sl.GetInt32() : 0;
            var startChar = startProp.TryGetProperty("character", out var sc) ? sc.GetInt32() : 0;
            var endLine = endProp.TryGetProperty("line", out var el) ? el.GetInt32() : startLine;
            var endChar = endProp.TryGetProperty("character", out var ec) ? ec.GetInt32() : startChar;

            var kind = item.TryGetProperty("kind", out var kindProp) ? kindProp.GetInt32() : 0;
            var container = item.TryGetProperty("containerName", out var cnProp) ? cnProp.GetString() ?? "" : "";

            matches.Add(new SymbolMatch(name, container, SymbolKindName(kind), new LspLocation(uri, startLine, startChar, endLine, endChar)));
        }

        return matches;
    }

    /// <summary>
    /// Finds the definition location(s) for the symbol at the given position via
    /// textDocument/definition. Handles null/undefined, single Location, array of
    /// Location, and LocationLink (targetUri / targetSelectionRange / targetRange).
    /// </summary>
    private async Task<LspLocation[]> FindDefinitionAsync(
        MessageLoop loop, string filePath, int line, int character, CancellationToken ct)
    {
        var fileUri = new Uri(filePath).ToString();
        await EnsureFileOpenAsync(loop, filePath, fileUri, ct).ConfigureAwait(false);

        var defParams = new
        {
            textDocument = new { uri = fileUri },
            position = new { line, character }
        };

        var result = await loop.SendRequestAsync("textDocument/definition", defParams, ct).ConfigureAwait(false);
        return ParseLocationOrLinkResult(result);
    }

    /// <summary>
    /// Finds implementation location(s) for the symbol at the given position via
    /// textDocument/implementation (e.g. classes implementing an interface, or overrides of
    /// an abstract member). Same Location/LocationLink result shape as textDocument/definition.
    /// </summary>
    private async Task<LspLocation[]> FindImplementationsAsync(
        MessageLoop loop, string filePath, int line, int character, CancellationToken ct)
    {
        var fileUri = new Uri(filePath).ToString();
        await EnsureFileOpenAsync(loop, filePath, fileUri, ct).ConfigureAwait(false);

        var implParams = new
        {
            textDocument = new { uri = fileUri },
            position = new { line, character }
        };

        var result = await loop.SendRequestAsync("textDocument/implementation", implParams, ct).ConfigureAwait(false);
        return ParseLocationOrLinkResult(result);
    }

    /// <summary>
    /// Finds hover contents (signature/doc-comment) for the symbol at the given position via
    /// textDocument/hover. Returns the raw hover text (markdown, exactly as the server sent
    /// it) or null when the server has no hover info for that position. Backs `tk sig`.
    /// </summary>
    private async Task<string?> FindHoverAsync(
        MessageLoop loop, string filePath, int line, int character, CancellationToken ct)
    {
        var fileUri = new Uri(filePath).ToString();
        await EnsureFileOpenAsync(loop, filePath, fileUri, ct).ConfigureAwait(false);

        var hoverParams = new
        {
            textDocument = new { uri = fileUri },
            position = new { line, character }
        };

        var result = await loop.SendRequestAsync("textDocument/hover", hoverParams, ct).ConfigureAwait(false);
        if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
            return null;

        return result.TryGetProperty("contents", out var contents) ? ExtractHoverText(contents) : null;
    }

    /// <summary>
    /// Extracts plain text out of an LSP hover "contents" value, which may be a bare string, a
    /// MarkupContent/MarkedString object ({ value } or { language, value }), or an array of
    /// either (joined with a blank line between entries).
    /// </summary>
    private static string? ExtractHoverText(JsonElement contents)
    {
        switch (contents.ValueKind)
        {
            case JsonValueKind.String:
                return contents.GetString();
            case JsonValueKind.Object:
                return contents.TryGetProperty("value", out var valueProp) ? valueProp.GetString() : null;
            case JsonValueKind.Array:
                var parts = new List<string>();
                foreach (var item in contents.EnumerateArray())
                {
                    var text = ExtractHoverText(item);
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add(text);
                }
                return parts.Count == 0 ? null : string.Join("\n\n", parts);
            default:
                return null;
        }
    }

    /// <summary>
    /// Shared result parsing for textDocument/definition and textDocument/implementation:
    /// both return null/undefined, a single Location, an array of Location, or an array of
    /// LocationLink (targetUri / targetSelectionRange / targetRange).
    /// </summary>
    private static LspLocation[] ParseLocationOrLinkResult(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
            return [];

        var locations = new List<LspLocation>();

        // Result may be a single object or an array; normalise to iteration.
        IEnumerable<JsonElement> elements = result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray()
            : [result];

        foreach (var el in elements)
        {
            // Determine uri: Location has "uri"; LocationLink has "targetUri".
            string? uri = null;
            if (el.TryGetProperty("uri", out var uriProp))
                uri = uriProp.GetString();
            else if (el.TryGetProperty("targetUri", out var targetUriProp))
                uri = targetUriProp.GetString();

            if (string.IsNullOrEmpty(uri))
                continue;

            // Determine range: Location has "range"; LocationLink has "targetSelectionRange" then "targetRange".
            JsonElement range = default;
            if (el.TryGetProperty("range", out var rangeProp))
                range = rangeProp;
            else if (el.TryGetProperty("targetSelectionRange", out var tsr))
                range = tsr;
            else if (el.TryGetProperty("targetRange", out var tr))
                range = tr;

            if (range.ValueKind == JsonValueKind.Undefined)
                continue;

            locations.Add(ParseRangeToLocation(uri, range));
        }

        return [.. locations];
    }

    /// <summary>
    /// Pulls diagnostics for a single file via textDocument/diagnostic (LSP 3.17 pull
    /// diagnostics — see docs/lsp-daemon-architecture.md for why pull was chosen over
    /// publishDiagnostics push). No previousResultId is sent, so the server always answers
    /// with a full report (never "unchanged").
    /// </summary>
    private async Task<LspDiagnostic[]> FindFileDiagnosticsAsync(
        MessageLoop loop, string filePath, CancellationToken ct)
    {
        var fileUri = new Uri(filePath).ToString();
        await EnsureFileOpenAsync(loop, filePath, fileUri, ct).ConfigureAwait(false);

        var diagParams = new { textDocument = new { uri = fileUri } };
        var result = await loop.SendRequestAsync("textDocument/diagnostic", diagParams, ct).ConfigureAwait(false);

        if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
            return [];

        // DocumentDiagnosticReport: { kind: "full"|"unchanged", resultId?, items?: Diagnostic[] }
        if (!result.TryGetProperty("kind", out var kindProp) || kindProp.GetString() != "full")
            return [];

        if (!result.TryGetProperty("items", out var itemsProp) || itemsProp.ValueKind != JsonValueKind.Array)
            return [];

        var diagnostics = new List<LspDiagnostic>();
        foreach (var item in itemsProp.EnumerateArray())
        {
            if (!item.TryGetProperty("range", out var range)) continue;
            if (!range.TryGetProperty("start", out var start)) continue;
            if (!range.TryGetProperty("end", out var end)) continue;

            var severity = item.TryGetProperty("severity", out var sevProp) ? sevProp.GetInt32() : 1;
            string? code = null;
            if (item.TryGetProperty("code", out var codeProp))
            {
                code = codeProp.ValueKind == JsonValueKind.String
                    ? codeProp.GetString()
                    : codeProp.ValueKind is JsonValueKind.Number
                        ? codeProp.GetRawText()
                        : null;
            }
            var message = item.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "" : "";

            diagnostics.Add(new LspDiagnostic(
                start.TryGetProperty("line", out var sl) ? sl.GetInt32() : 0,
                start.TryGetProperty("character", out var sc) ? sc.GetInt32() : 0,
                end.TryGetProperty("line", out var el) ? el.GetInt32() : 0,
                end.TryGetProperty("character", out var ec) ? ec.GetInt32() : 0,
                DiagnosticSeverityName(severity),
                code,
                message));
        }

        return [.. diagnostics];
    }

    private static string DiagnosticSeverityName(int severity) => severity switch
    {
        1 => "error",
        2 => "warning",
        3 => "info",
        4 => "hint",
        _ => "info",
    };

    private static int DiagnosticSeverityNumber(string severity) => severity switch
    {
        "error" => 1,
        "warning" => 2,
        "info" => 3,
        "hint" => 4,
        _ => 3,
    };

    /// <summary>
    /// The two diagnostic families `tk fix` is allowed to act on: CS0246 ("type or namespace
    /// could not be found", a missing-using candidate) and CS8019/IDE0005 ("unnecessary using
    /// directive", a remove-using candidate). Nothing else is ever sent to codeAction — the
    /// safe subset is enforced here, before any protocol round-trip.
    /// </summary>
    private static readonly HashSet<string> FixableDiagnosticCodes = new(StringComparer.Ordinal)
    {
        "CS0246", "CS8019", "IDE0005",
    };

    private enum UsingFixKind { Add, Remove }

    // Roslyn's own quickfix title for an add-using action is literally the using directive
    // text it would insert (e.g. "using System.Text.RegularExpressions;").
    private static readonly System.Text.RegularExpressions.Regex UsingDirectiveTitleRegex =
        new(@"^using\s+[A-Za-z_][\w.]*;$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Computes the restricted "add missing using / remove unnecessary using" fix for a single
    /// file: pulls diagnostics, keeps only <see cref="FixableDiagnosticCodes"/>, requests a
    /// textDocument/codeAction per diagnostic, and keeps only actions whose title matches the
    /// safe subset (<see cref="IsSafeFixTitle"/>) — never anything else, and never partially
    /// applies an action that would require a workspace/executeCommand round-trip this daemon
    /// does not implement (see <see cref="RequestUsingCodeActionAsync"/>). Backs `tk fix`.
    /// </summary>
    private async Task<(FileEdits[] Edits, FixSummary Summary)> ComputeUsingFixesAsync(
        MessageLoop loop, string filePath, CancellationToken ct)
    {
        var fileUri = new Uri(filePath).ToString();
        await EnsureFileOpenAsync(loop, filePath, fileUri, ct).ConfigureAwait(false);

        var diagnostics = await FindFileDiagnosticsAsync(loop, filePath, ct).ConfigureAwait(false);
        var relevant = diagnostics.Where(d => d.Code is not null && FixableDiagnosticCodes.Contains(d.Code)).ToList();

        if (relevant.Count == 0)
            return ([], new FixSummary(true, 0, 0, null));

        var collectedEdits = new List<RenameTextEdit>();
        var added = 0;
        var removed = 0;
        var sawUnresolvable = false;

        foreach (var diag in relevant)
        {
            var action = await RequestUsingCodeActionAsync(loop, fileUri, diag, ct).ConfigureAwait(false);
            if (action is null)
            {
                sawUnresolvable = true;
                continue;
            }

            var (kind, edits) = action.Value;
            collectedEdits.AddRange(edits);
            if (kind == UsingFixKind.Add) added++;
            else removed++;
        }

        if (collectedEdits.Count == 0)
        {
            var note = sawUnresolvable
                ? "server offered no matching add/remove-using quick fix for the detected diagnostics (or it would require an unsupported workspace/executeCommand round-trip)"
                : null;
            return ([], new FixSummary(!sawUnresolvable, 0, 0, note));
        }

        return ([new FileEdits(fileUri, [.. collectedEdits])], new FixSummary(true, added, removed, null));
    }

    /// <summary>
    /// Requests textDocument/codeAction for one diagnostic and returns the edits of the first
    /// action whose title is in the safe add/remove-using subset — resolving it via
    /// codeAction/resolve first if the server didn't include an "edit" inline. Returns null if
    /// no safe action was offered, or the only safe-titled action never yields a concrete edit
    /// (e.g. it only carries a "command" — that would need workspace/executeCommand, which this
    /// daemon does not implement; skipped rather than half-applied).
    /// </summary>
    private async Task<(UsingFixKind Kind, List<RenameTextEdit> Edits)?> RequestUsingCodeActionAsync(
        MessageLoop loop, string fileUri, LspDiagnostic diag, CancellationToken ct)
    {
        var range = new
        {
            start = new { line = diag.Line, character = diag.Character },
            end = new { line = diag.EndLine, character = diag.EndChar }
        };
        var wireDiag = new { range, severity = DiagnosticSeverityNumber(diag.Severity), code = diag.Code, message = diag.Message };

        var codeActionParams = new
        {
            textDocument = new { uri = fileUri },
            range,
            context = new { diagnostics = new[] { wireDiag }, only = new[] { "quickfix" } }
        };

        var result = await loop.SendRequestAsync("textDocument/codeAction", codeActionParams, ct).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in result.EnumerateArray())
        {
            if (!item.TryGetProperty("title", out var titleProp))
                continue;
            var title = titleProp.GetString() ?? "";

            UsingFixKind kind;
            if (UsingDirectiveTitleRegex.IsMatch(title))
                kind = UsingFixKind.Add;
            else if (title.Equals("Remove Unnecessary Usings", StringComparison.OrdinalIgnoreCase))
                kind = UsingFixKind.Remove;
            else
                continue; // outside the safe subset — never requested to resolve, never applied

            JsonElement? edit = item.TryGetProperty("edit", out var editProp) ? editProp : null;
            if (edit is null && item.TryGetProperty("data", out _))
            {
                var resolved = await loop.SendRequestAsync("codeAction/resolve", item, ct).ConfigureAwait(false);
                if (resolved.TryGetProperty("edit", out var resolvedEdit))
                    edit = resolvedEdit;
            }

            if (edit is null)
                continue; // no concrete edit available without workspace/executeCommand — skip

            var edits = ExtractEditsForFile(edit.Value, fileUri);
            if (edits.Count == 0)
                continue;

            return (kind, edits);
        }

        return null;
    }

    /// <summary>
    /// Extracts the TextEdits targeting <paramref name="fileUri"/> out of a WorkspaceEdit,
    /// handling both the "changes" (uri -> TextEdit[] map) and "documentChanges" (array of
    /// {textDocument:{uri}, edits}) shapes — same two shapes <see cref="RenameAsync"/> parses.
    /// Edits for any other file are dropped: `tk fix` is single-file by design.
    /// </summary>
    private static List<RenameTextEdit> ExtractEditsForFile(JsonElement edit, string fileUri)
    {
        var result = new List<RenameTextEdit>();

        if (edit.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in changes.EnumerateObject())
            {
                if (!string.Equals(prop.Name, fileUri, StringComparison.Ordinal)) continue;
                result.AddRange(ParseTextEdits(prop.Value));
            }
        }

        if (edit.TryGetProperty("documentChanges", out var docChanges) && docChanges.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in docChanges.EnumerateArray())
            {
                if (!item.TryGetProperty("textDocument", out var td)) continue;
                if (!td.TryGetProperty("uri", out var uriProp)) continue;
                if (!string.Equals(uriProp.GetString(), fileUri, StringComparison.Ordinal)) continue;
                if (!item.TryGetProperty("edits", out var editsProp)) continue;
                result.AddRange(ParseTextEdits(editsProp));
            }
        }

        return result;
    }

    /// <summary>
    /// Finds incoming callers of the symbol at the given position using the LSP call hierarchy
    /// (textDocument/prepareCallHierarchy then callHierarchy/incomingCalls).
    /// </summary>
    private Task<CallerInfo[]> FindIncomingCallsAsync(
        MessageLoop loop, string filePath, int line, int character, CancellationToken ct) =>
        FindCallHierarchyAsync(loop, filePath, line, character, "callHierarchy/incomingCalls", "from", ct);

    /// <summary>
    /// Finds outgoing callees of the symbol at the given position using the LSP call hierarchy
    /// (textDocument/prepareCallHierarchy then callHierarchy/outgoingCalls). Backs `tk calls`.
    /// KNOWN RISK: some Roslyn language-server builds do not implement outgoingCalls and
    /// answer with an empty array even for a method that provably calls others — the caller
    /// (CallsCommand) surfaces that ambiguity rather than reporting a false "no outgoing calls".
    /// </summary>
    private Task<CallerInfo[]> FindOutgoingCallsAsync(
        MessageLoop loop, string filePath, int line, int character, CancellationToken ct) =>
        FindCallHierarchyAsync(loop, filePath, line, character, "callHierarchy/outgoingCalls", "to", ct);

    /// <summary>
    /// Shared implementation for both call-hierarchy directions: prepares the hierarchy item at
    /// the given position, then sends <paramref name="callMethod"/> ("callHierarchy/incomingCalls"
    /// or "callHierarchy/outgoingCalls") and reads the target item under <paramref
    /// name="itemField"/> ("from" for incoming, "to" for outgoing).
    ///
    /// Call-site ("fromRanges") URI differs by direction per the LSP spec: for incoming calls
    /// the ranges live inside the *caller's* own file (the "from" item), but for outgoing calls
    /// they live inside the *original* file at (filePath,line,character) — the item we started
    /// prepareCallHierarchy on, not the "to" item's file. <paramref name="callSitesInSourceFile"/>
    /// selects which URI the call sites are stamped with.
    /// </summary>
    private async Task<CallerInfo[]> FindCallHierarchyAsync(
        MessageLoop loop, string filePath, int line, int character,
        string callMethod, string itemField, CancellationToken ct)
    {
        var callSitesInSourceFile = itemField == "to";
        var fileUri = new Uri(filePath).ToString();
        await EnsureFileOpenAsync(loop, filePath, fileUri, ct).ConfigureAwait(false);

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

        if (callResult.ValueKind == JsonValueKind.Null || callResult.ValueKind == JsonValueKind.Undefined)
            return [];

        var results = new List<CallerInfo>();
        foreach (var call in callResult.EnumerateArray())
        {
            if (!call.TryGetProperty(itemField, out var target)) continue;

            var targetName = target.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
            var targetKind = target.TryGetProperty("kind", out var kp) ? kp.GetInt32() : 0;
            var targetDetail = target.TryGetProperty("detail", out var dp) ? dp.GetString() ?? "" : "";

            // selectionRange preferred over range for the symbol name position
            JsonElement selRange;
            if (!target.TryGetProperty("selectionRange", out selRange))
                if (!target.TryGetProperty("range", out selRange))
                    continue;

            if (!target.TryGetProperty("uri", out var targetUriProp)) continue;
            var targetUri = targetUriProp.GetString() ?? "";

            var targetLoc = ParseRangeToLocation(targetUri, selRange);
            var callSiteUri = callSitesInSourceFile ? fileUri : targetUri;

            // Parse fromRanges (the actual call sites)
            var callSites = new List<LspLocation>();
            if (call.TryGetProperty("fromRanges", out var fromRanges) && fromRanges.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in fromRanges.EnumerateArray())
                    callSites.Add(ParseRangeToLocation(callSiteUri, r));
            }

            results.Add(new CallerInfo(targetName, targetDetail, SymbolKindName(targetKind), targetLoc, [.. callSites]));
        }

        return [.. results];
    }

    private static LspLocation ParseRangeToLocation(string uri, JsonElement range)
    {
        var start = range.TryGetProperty("start", out var sp) ? sp : default;
        var end = range.TryGetProperty("end", out var ep) ? ep : default;
        var sl = start.ValueKind != JsonValueKind.Undefined && start.TryGetProperty("line", out var slp) ? slp.GetInt32() : 0;
        var sc = start.ValueKind != JsonValueKind.Undefined && start.TryGetProperty("character", out var scp) ? scp.GetInt32() : 0;
        var el = end.ValueKind != JsonValueKind.Undefined && end.TryGetProperty("line", out var elp) ? elp.GetInt32() : sl;
        var ec = end.ValueKind != JsonValueKind.Undefined && end.TryGetProperty("character", out var ecp) ? ecp.GetInt32() : sc;
        return new LspLocation(uri, sl, sc, el, ec);
    }

    private static string SymbolKindName(int kind) => kind switch
    {
        5 => "class",
        6 => "method",
        7 => "property",
        8 => "field",
        9 => "constructor",
        10 => "enum",
        11 => "interface",
        12 => "function",
        13 => "variable",
        22 => "enumMember",
        23 => "struct",
        26 => "typeParameter",
        _ => "symbol",
    };

    private static RenameTextEdit[] ParseTextEdits(JsonElement editsArray)
    {
        var list = new List<RenameTextEdit>();
        foreach (var edit in editsArray.EnumerateArray())
        {
            var range = edit.GetProperty("range");
            var start = range.GetProperty("start");
            var end = range.GetProperty("end");
            var newText = edit.GetProperty("newText").GetString() ?? "";
            list.Add(new RenameTextEdit(
                start.GetProperty("line").GetInt32(),
                start.GetProperty("character").GetInt32(),
                end.GetProperty("line").GetInt32(),
                end.GetProperty("character").GetInt32(),
                newText));
        }
        return [.. list];
    }

    /// <summary>
    /// Sends textDocument/didOpen for a file the first time it's queried; on later calls,
    /// resyncs it via <see cref="DecideSyncAction"/> against the file's current mtime — a
    /// didChange (full-document replace) if it was edited on disk since the last open/sync
    /// (e.g. by the agent, with no `dotnet build` in between), or a didClose if it was
    /// deleted. Roslyn requires the document to be open before it will answer position-based
    /// queries; otherwise it faults with "Unexpected null".
    /// </summary>
    private async Task EnsureFileOpenAsync(MessageLoop loop, string filePath, string fileUri, CancellationToken ct)
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
                Log($"didOpen {fileUri}");
                _openDocs[fileUri] = new OpenDocState(1, currentMtime);

                // Give the server a moment to register the document before querying.
                await Task.Delay(300, ct).ConfigureAwait(false);
                return;
            }

            switch (DecideSyncAction(state.Mtime, fileExists, currentMtime))
            {
                case SyncAction.Close:
                    await loop.SendNotificationAsync("textDocument/didClose", new
                    {
                        textDocument = new { uri = fileUri }
                    }, ct).ConfigureAwait(false);
                    _openDocs.Remove(fileUri);
                    Log($"didClose (file missing) {fileUri}");
                    break;

                case SyncAction.Change:
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
                    Log($"didClose+didOpen (stale) {fileUri}");

                    // Give the server a moment to reprocess before querying.
                    await Task.Delay(300, ct).ConfigureAwait(false);
                    break;

                case SyncAction.None:
                default:
                    break;
            }
        }
        finally
        {
            _openLock.Release();
        }
    }

    private readonly record struct ResolvedTarget(string Path, int Line, int Character);

    /// <summary>
    /// Shared "file position, or resolve a symbol name via workspace/symbol" resolution used
    /// by the newer position-or-symbol request kinds (sig, calls) — the same resolution
    /// def/impl/callers/rename already do inline. Returns exactly one of: a resolved position,
    /// a list of ambiguous candidates (more than one match), or an error message (no match, or
    /// neither a position nor a symbol was given).
    /// </summary>
    private async Task<(ResolvedTarget? Position, SymbolMatch[]? Candidates, string? Error)> ResolveTargetAsync(
        MessageLoop loop, string? filePath, int line, int character, string? symbol, string what, CancellationToken ct)
    {
        if (filePath is not null)
            return (new ResolvedTarget(filePath, line, character), null, null);

        if (symbol is not null)
        {
            var matches = await ResolveSymbolAsync(loop, symbol, ct).ConfigureAwait(false);
            if (matches.Count == 0)
                return (null, null, $"symbol '{symbol}' not found");
            if (matches.Count > 1)
                return (null, matches.ToArray(), null);

            var loc = matches[0].Location;
            return (new ResolvedTarget(new Uri(loc.Uri).LocalPath, loc.StartLine, loc.StartChar), null, null);
        }

        return (null, null, $"{what} requires a file position or symbol name");
    }

    private async Task HandleClientAsync(Socket client, MessageLoop loop, CancellationToken ct)
    {
        using var stream = new NetworkStream(client, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (line is null)
            return;

        DaemonResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<DaemonRequest>(line, LspMessage.Options);
            if (request is null)
            {
                response = new DaemonResponse(false, "Invalid request", null);
            }
            else if (request.Method == "stop")
            {
                response = new DaemonResponse(true, null, null);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, LspMessage.Options)).ConfigureAwait(false);
                // Unwind the host's accept loop so its cleanup actually runs: kills the
                // backend child, deletes the socket/pid files, and lets this process exit.
                // Previously this handler only replied success without triggering any of that,
                // leaving the daemon (and its Roslyn child) running forever after `lsp stop`.
                _host?.RequestShutdown();
                return;
            }
            else if (request.Method == "symbols" && request.Symbol is not null)
            {
                await WaitForReadyAsync(ct).ConfigureAwait(false);
                Log($"symbols query: {request.Symbol}");
                var matches = await ResolveSymbolAsync(loop, request.Symbol, ct).ConfigureAwait(false);
                Log($"symbols query done, {matches.Count} matches");
                response = new DaemonResponse(true, null, null) with { Candidates = matches.ToArray() };
            }
            else if (request.Method == "refs" && request.FilePath is not null)
            {
                // Wait for daemon to be ready (client may connect during Loading)
                await WaitForReadyAsync(ct).ConfigureAwait(false);
                Log($"refs query: {request.FilePath}:{request.Line}:{request.Character}");

                var locs = await FindReferencesAsync(loop, request.FilePath, request.Line, request.Character, ct).ConfigureAwait(false);
                Log($"refs query done, {locs.Length} locations");
                response = new DaemonResponse(true, null, locs);
            }
            else if (request.Method == "refs" && request.Symbol is not null)
            {
                await WaitForReadyAsync(ct).ConfigureAwait(false);
                Log($"refs by symbol: {request.Symbol}");

                var matches = await ResolveSymbolAsync(loop, request.Symbol, ct).ConfigureAwait(false);
                if (matches.Count == 0)
                {
                    response = new DaemonResponse(false, $"symbol '{request.Symbol}' not found", null);
                }
                else if (matches.Count == 1)
                {
                    var loc = matches[0].Location;
                    var path = new Uri(loc.Uri).LocalPath;
                    var locs = await FindReferencesAsync(loop, path, loc.StartLine, loc.StartChar, ct).ConfigureAwait(false);
                    Log($"refs by symbol done, {locs.Length} locations");
                    response = new DaemonResponse(true, null, locs);
                }
                else
                {
                    Log($"refs by symbol ambiguous, {matches.Count} candidates");
                    response = new DaemonResponse(true, null, null) with { Candidates = matches.ToArray() };
                }
            }
            else if (request.Method == "callers")
            {
                await WaitForReadyAsync(ct).ConfigureAwait(false);

                string callersPath;
                int callersLine;
                int callersChar;

                if (request.FilePath is not null)
                {
                    callersPath = request.FilePath;
                    callersLine = request.Line;
                    callersChar = request.Character;
                }
                else if (request.Symbol is not null)
                {
                    var matches = await ResolveSymbolAsync(loop, request.Symbol, ct).ConfigureAwait(false);
                    if (matches.Count == 0)
                    {
                        response = new DaemonResponse(false, $"symbol '{request.Symbol}' not found", null);
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response, LspMessage.Options)).ConfigureAwait(false);
                        return;
                    }
                    if (matches.Count > 1)
                    {
                        response = new DaemonResponse(true, null, null) with { Candidates = matches.ToArray() };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response, LspMessage.Options)).ConfigureAwait(false);
                        return;
                    }
                    var loc = matches[0].Location;
                    callersPath = new Uri(loc.Uri).LocalPath;
                    callersLine = loc.StartLine;
                    callersChar = loc.StartChar;
                }
                else
                {
                    response = new DaemonResponse(false, "callers requires a file position or symbol name", null);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, LspMessage.Options)).ConfigureAwait(false);
                    return;
                }

                Log($"callers query: {callersPath}:{callersLine}:{callersChar}");
                var callers = await FindIncomingCallsAsync(loop, callersPath, callersLine, callersChar, ct).ConfigureAwait(false);
                Log($"callers query done, {callers.Length} callers");
                response = new DaemonResponse(true, null, null) with { Callers = callers };
            }
            else if (request.Method == "def")
            {
                await WaitForReadyAsync(ct).ConfigureAwait(false);

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
                    var matches = await ResolveSymbolAsync(loop, request.Symbol, ct).ConfigureAwait(false);
                    if (matches.Count == 0)
                    {
                        response = new DaemonResponse(false, $"symbol '{request.Symbol}' not found", null);
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response, LspMessage.Options)).ConfigureAwait(false);
                        return;
                    }
                    if (matches.Count > 1)
                    {
                        response = new DaemonResponse(true, null, null) with { Candidates = matches.ToArray() };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response, LspMessage.Options)).ConfigureAwait(false);
                        return;
                    }
                    var loc = matches[0].Location;
                    defPath = new Uri(loc.Uri).LocalPath;
                    defLine = loc.StartLine;
                    defChar = loc.StartChar;
                }
                else
                {
                    response = new DaemonResponse(false, "def requires a file position or symbol name", null);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, LspMessage.Options)).ConfigureAwait(false);
                    return;
                }

                Log($"def query: {defPath}:{defLine}:{defChar}");
                var locs = await FindDefinitionAsync(loop, defPath, defLine, defChar, ct).ConfigureAwait(false);
                Log($"def query done, {locs.Length} locations");
                response = new DaemonResponse(true, null, locs);
            }
            else if (request.Method == "impl")
            {
                await WaitForReadyAsync(ct).ConfigureAwait(false);

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
                    var matches = await ResolveSymbolAsync(loop, request.Symbol, ct).ConfigureAwait(false);
                    if (matches.Count == 0)
                    {
                        response = new DaemonResponse(false, $"symbol '{request.Symbol}' not found", null);
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response, LspMessage.Options)).ConfigureAwait(false);
                        return;
                    }
                    if (matches.Count > 1)
                    {
                        response = new DaemonResponse(true, null, null) with { Candidates = matches.ToArray() };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response, LspMessage.Options)).ConfigureAwait(false);
                        return;
                    }
                    var loc = matches[0].Location;
                    implPath = new Uri(loc.Uri).LocalPath;
                    implLine = loc.StartLine;
                    implChar = loc.StartChar;
                }
                else
                {
                    response = new DaemonResponse(false, "impl requires a file position or symbol name", null);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, LspMessage.Options)).ConfigureAwait(false);
                    return;
                }

                Log($"impl query: {implPath}:{implLine}:{implChar}");
                var implLocs = await FindImplementationsAsync(loop, implPath, implLine, implChar, ct).ConfigureAwait(false);
                Log($"impl query done, {implLocs.Length} locations");
                response = new DaemonResponse(true, null, implLocs);
            }
            else if (request.Method == "sig")
            {
                await WaitForReadyAsync(ct).ConfigureAwait(false);

                var (sigTarget, sigCandidates, sigError) = await ResolveTargetAsync(
                    loop, request.FilePath, request.Line, request.Character, request.Symbol, "sig", ct).ConfigureAwait(false);
                if (sigError is not null)
                {
                    response = new DaemonResponse(false, sigError, null);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, LspMessage.Options)).ConfigureAwait(false);
                    return;
                }
                if (sigCandidates is not null)
                {
                    response = new DaemonResponse(true, null, null) with { Candidates = sigCandidates };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, LspMessage.Options)).ConfigureAwait(false);
                    return;
                }

                var sig = sigTarget!.Value;
                Log($"sig query: {sig.Path}:{sig.Line}:{sig.Character}");
                var hoverText = await FindHoverAsync(loop, sig.Path, sig.Line, sig.Character, ct).ConfigureAwait(false);
                Log($"sig query done, hover {(hoverText is null ? "empty" : "present")}");
                var hover = hoverText is null ? null : new HoverResult(new Uri(sig.Path).ToString(), sig.Line, sig.Character, hoverText);
                response = new DaemonResponse(true, null, null) with { Hover = hover };
            }
            else if (request.Method == "calls")
            {
                await WaitForReadyAsync(ct).ConfigureAwait(false);

                var (callsTarget, callsCandidates, callsError) = await ResolveTargetAsync(
                    loop, request.FilePath, request.Line, request.Character, request.Symbol, "calls", ct).ConfigureAwait(false);
                if (callsError is not null)
                {
                    response = new DaemonResponse(false, callsError, null);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, LspMessage.Options)).ConfigureAwait(false);
                    return;
                }
                if (callsCandidates is not null)
                {
                    response = new DaemonResponse(true, null, null) with { Candidates = callsCandidates };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, LspMessage.Options)).ConfigureAwait(false);
                    return;
                }

                var calls = callsTarget!.Value;
                Log($"calls query: {calls.Path}:{calls.Line}:{calls.Character}");
                var callees = await FindOutgoingCallsAsync(loop, calls.Path, calls.Line, calls.Character, ct).ConfigureAwait(false);
                Log($"calls query done, {callees.Length} callees");
                response = new DaemonResponse(true, null, null) with { Callees = callees };
            }
            else if (request.Method == "sym" && request.Symbol is not null)
            {
                await WaitForReadyAsync(ct).ConfigureAwait(false);
                Log($"sym query: {request.Symbol}");
                var symMatches = await ResolveSymbolAsync(loop, request.Symbol, ct, exactMatchOnly: false).ConfigureAwait(false);
                Log($"sym query done, {symMatches.Count} matches");
                response = new DaemonResponse(true, null, null) with { Candidates = symMatches.ToArray() };
            }
            else if (request.Method == "fix" && request.FilePath is not null)
            {
                await WaitForReadyAsync(ct).ConfigureAwait(false);
                Log($"fix query: {request.FilePath}");
                var (fixEdits, fixSummary) = await ComputeUsingFixesAsync(loop, request.FilePath, ct).ConfigureAwait(false);
                Log($"fix query done, supported={fixSummary.Supported} added={fixSummary.UsingsAdded} removed={fixSummary.UsingsRemoved}");
                response = new DaemonResponse(true, null, null) with { Edits = fixEdits, Fix = fixSummary };
            }
            else if (request.Method == "diag" && request.Paths is { Length: > 0 } diagPaths)
            {
                await WaitForReadyAsync(ct).ConfigureAwait(false);
                Log($"diag query: {diagPaths.Length} file(s)");

                var byFile = new List<FileDiagnostics>();
                foreach (var path in diagPaths)
                {
                    var diags = await FindFileDiagnosticsAsync(loop, path, ct).ConfigureAwait(false);
                    byFile.Add(new FileDiagnostics(new Uri(path).ToString(), diags));
                }

                Log($"diag query done, {byFile.Sum(f => f.Diagnostics.Length)} diagnostics across {byFile.Count} file(s)");
                response = new DaemonResponse(true, null, null) with { DiagnosticsByFile = byFile.ToArray() };
            }
            else if (request.Method == "rename" && request.FilePath is not null && request.NewName is not null)
            {
                await WaitForReadyAsync(ct).ConfigureAwait(false);
                Log($"rename: {request.FilePath}:{request.Line}:{request.Character} -> {request.NewName}");
                var edits = await RenameAsync(loop, request.FilePath, request.Line, request.Character, request.NewName, ct).ConfigureAwait(false);
                var n = edits.Sum(f => f.Edits.Length);
                Log($"rename done, {n} edits in {edits.Length} files");
                response = new DaemonResponse(true, null, null) with { Edits = edits };
            }
            else
            {
                response = new DaemonResponse(false, $"Unknown method: {request.Method}", null);
            }
        }
        catch (Exception ex)
        {
            Log($"client request error: {ex.Message}");
            response = new DaemonResponse(false, ex.Message, null);
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response, LspMessage.Options)).ConfigureAwait(false);
    }

    /// <summary>
    /// The action to take when syncing an open document for freshness.
    /// </summary>
    public enum SyncAction { None, Change, Close }

    /// <summary>
    /// Pure helper: decides what sync action to take for an already-opened document.
    /// Returns <see cref="SyncAction.Close"/> if the file no longer exists,
    /// <see cref="SyncAction.Change"/> if the file has been modified since it was opened,
    /// or <see cref="SyncAction.None"/> if no update is needed.
    /// </summary>
    public static SyncAction DecideSyncAction(DateTime storedMtime, bool fileExists, DateTime currentMtime)
    {
        if (!fileExists) return SyncAction.Close;
        if (currentMtime > storedMtime) return SyncAction.Change;
        return SyncAction.None;
    }

    private void Log(string message)
    {
        try
        {
            var line = $"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}{Environment.NewLine}";
            File.AppendAllText(_logPath, line);
        }
        catch { /* log failures must never crash the daemon */ }
    }
}
