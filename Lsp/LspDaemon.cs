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

    // State machine
    private volatile DaemonState _state = DaemonState.Loading;
    private volatile string? _failReason;
    private readonly TaskCompletionSource<bool> _readyTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, idleTimer.Token);

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
                        publishDiagnostics = new { relatedInformation = true }
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
                return;
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
