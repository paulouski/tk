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
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(120);

    private readonly string _workspaceRoot;
    private readonly ILanguageBackend _backend;
    private readonly string _logPath;
    private readonly string _pidPath;

    // State machine
    private volatile DaemonState _state = DaemonState.Loading;
    private volatile string? _failReason;
    private readonly TaskCompletionSource<bool> _readyTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Cancelled when a client sends the "stop" request, so the accept loop unwinds and the
    // daemon (and its Roslyn child, killed in the RunAsync finally block) actually exits.
    private readonly CancellationTokenSource _shutdownRequested = new();

    // URIs already sent via textDocument/didOpen (required before queries; the server
    // throws "Unexpected null" in FindAllReferencesHandler for an unopened document).
    private readonly HashSet<string> _openedUris = new(StringComparer.Ordinal);
    private readonly object _openLock = new();

    public DaemonState State => _state;

    public LspDaemon(string workspaceRoot, ILanguageBackend backend)
    {
        _workspaceRoot = workspaceRoot;
        _backend = backend;
        _logPath = DaemonSocket.GetLogPath(workspaceRoot);
        _pidPath = DaemonSocket.GetPidPath(workspaceRoot);
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

        // ── Step 1: Bind the unix socket FIRST ────────────────────────────────
        var socketPath = DaemonSocket.GetSocketPath(_workspaceRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);

        if (File.Exists(socketPath))
            File.Delete(socketPath);

        using var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        server.Bind(new UnixDomainSocketEndPoint(socketPath));
        server.Listen(8);
        Log($"socket bound: {socketPath}");

        // ── Step 2: Launch handshake in background ────────────────────────────
        using var handshakeCts = new CancellationTokenSource(HandshakeTimeout);
        using var combinedHandshake = CancellationTokenSource.CreateLinkedTokenSource(ct, handshakeCts.Token);

        // Diagnostic: point the server's own extension logs to a dir next to our daemon log.
        var extLogDir = Path.Combine(Path.GetDirectoryName(_logPath)!, "serverlogs");
        Directory.CreateDirectory(extLogDir);

        var args = _backend.GetLaunchArgs(serverPath)
            .Concat(["--extensionLogDirectory", extLogDir])
            .ToArray();
        var executable = args[0];
        var launchArgs = string.Join(" ", args[1..].Select(a => $"\"{a}\""));

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = executable,
            Arguments = launchArgs,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _workspaceRoot,
        };

        // Detect an early server crash (SIGABRT on startup): turn a silent 120s
        // handshake hang into an immediate Failed with the exit code.
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            int code;
            try { code = process.ExitCode; } catch { code = -1; }
            Log($"server process EXITED code={code}");
            if (_state == DaemonState.Loading)
            {
                TransitionToFailed($"server-exited code={code}");
                try { handshakeCts.Cancel(); } catch { }
            }
        };

        process.Start();
        Log($"server process launched, pid={process.Id}");

        // Persist both PIDs so `lsp stop`/`lsp status` can verify and, if necessary,
        // forcibly terminate this daemon and its Roslyn child even if the socket-based
        // graceful stop below is unresponsive or the socket file itself is gone.
        DaemonSocket.WritePidInfo(_pidPath, new DaemonPidInfo(Environment.ProcessId, process.Id));

        // Pump server stderr into the daemon log — the one channel that shows
        // MSBuild/SDK/BuildHost failures (previously discarded).
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) Log($"[stderr] {e.Data}");
        };
        process.BeginErrorReadLine();

        var processInput = process.StandardOutput.BaseStream;
        var processOutput = process.StandardInput.BaseStream;

        await using var loop = new MessageLoop(processInput, processOutput);
        loop.Trace = m => Log($"<< {m}");

        var handshakeTask = Task.Run(
            () => HandshakeAsync(loop, _workspaceRoot, combinedHandshake.Token),
            combinedHandshake.Token);

        // ── Step 3: Accept clients immediately (socket-first) ─────────────────
        using var idleTimer = new CancellationTokenSource(IdleTimeout);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, idleTimer.Token, _shutdownRequested.Token);

        try
        {
            while (!combined.Token.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await server.AcceptAsync(combined.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                Log($"client connected");
                idleTimer.CancelAfter(IdleTimeout);
                _ = Task.Run(async () =>
                {
                    try { await HandleClientAsync(client, loop, combined.Token).ConfigureAwait(false); }
                    catch (Exception ex) { Log($"client handler error: {ex}"); }
                    finally { client.Dispose(); }
                }, combined.Token);
            }
        }
        finally
        {
            if (File.Exists(socketPath))
                File.Delete(socketPath);
            if (File.Exists(_pidPath))
                try { File.Delete(_pidPath); } catch { }
            try { process.Kill(entireProcessTree: true); } catch { }
            Log("daemon stopped");
        }

        // Await handshake to propagate exceptions
        try { await handshakeTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"handshake task exception: {ex}"); }
    }

    private async Task HandshakeAsync(MessageLoop loop, string workspaceRoot, CancellationToken ct)
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
                        callHierarchy = new { dynamicRegistration = false },
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
            throw;
        }
        catch (Exception ex)
        {
            Log($"handshake EXCEPTION: {ex}");
            TransitionToFailed(ex.Message);
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
    /// matching workspace symbols via workspace/symbol. Returns only results whose 'name'
    /// field exactly matches the simple name (the substring after the last '.').
    /// </summary>
    private async Task<List<SymbolMatch>> ResolveSymbolAsync(MessageLoop loop, string symbol, CancellationToken ct)
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
            if (name != simpleName) continue;

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
    /// Finds incoming callers of the symbol at the given position using the LSP call hierarchy.
    /// Sends textDocument/prepareCallHierarchy then callHierarchy/incomingCalls.
    /// </summary>
    private async Task<CallerInfo[]> FindIncomingCallsAsync(
        MessageLoop loop, string filePath, int line, int character, CancellationToken ct)
    {
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

        var incomingParams = new { item = itemEl };
        var incomingResult = await loop.SendRequestAsync("callHierarchy/incomingCalls", incomingParams, ct).ConfigureAwait(false);

        if (incomingResult.ValueKind == JsonValueKind.Null || incomingResult.ValueKind == JsonValueKind.Undefined)
            return [];

        var callers = new List<CallerInfo>();
        foreach (var call in incomingResult.EnumerateArray())
        {
            if (!call.TryGetProperty("from", out var from)) continue;

            var callerName = from.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
            var callerKind = from.TryGetProperty("kind", out var kp) ? kp.GetInt32() : 0;
            var callerDetail = from.TryGetProperty("detail", out var dp) ? dp.GetString() ?? "" : "";

            // selectionRange preferred over range for the symbol name position
            JsonElement selRange;
            if (!from.TryGetProperty("selectionRange", out selRange))
                if (!from.TryGetProperty("range", out selRange))
                    continue;

            if (!from.TryGetProperty("uri", out var callerUriProp)) continue;
            var callerUri = callerUriProp.GetString() ?? "";

            var callerLoc = ParseRangeToLocation(callerUri, selRange);

            // Parse fromRanges (the actual call sites inside the caller)
            var callSites = new List<LspLocation>();
            if (call.TryGetProperty("fromRanges", out var fromRanges) && fromRanges.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in fromRanges.EnumerateArray())
                    callSites.Add(ParseRangeToLocation(callerUri, r));
            }

            callers.Add(new CallerInfo(callerName, callerDetail, SymbolKindName(callerKind), callerLoc, [.. callSites]));
        }

        return [.. callers];
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
    /// Sends textDocument/didOpen for a file once. Roslyn requires the document to be open
    /// before it will answer position-based queries; otherwise it faults with "Unexpected null".
    /// </summary>
    private async Task EnsureFileOpenAsync(MessageLoop loop, string filePath, string fileUri, CancellationToken ct)
    {
        lock (_openLock)
        {
            if (!_openedUris.Add(fileUri))
                return;
        }

        string text;
        try { text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false); }
        catch { return; }

        await loop.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri = fileUri, languageId = "csharp", version = 1, text }
        }, ct).ConfigureAwait(false);
        Log($"didOpen {fileUri}");

        // Give the server a moment to register the document before querying.
        await Task.Delay(300, ct).ConfigureAwait(false);
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
                // Unwind the accept loop so RunAsync's finally block actually runs: kills the
                // Roslyn child, deletes the socket/pid files, and lets this process exit.
                // Previously this handler only replied success without triggering any of that,
                // leaving the daemon (and its Roslyn child) running forever after `lsp stop`.
                _shutdownRequested.Cancel();
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
