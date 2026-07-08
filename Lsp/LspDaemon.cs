using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tk.Lsp.Protocol;
using Tk.Lsp.RequestHandlers;

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
/// The LSP daemon process. Launches the language server, performs the handshake, then serves
/// textDocument/references requests over a unix socket.
///
/// Architecture (socket-first):
///   1. Socket is bound and listening IMMEDIATELY on startup, before any server interaction.
///   2. Server launch + handshake run in a background task.
///   3. Clients that connect while state=Loading are held until Ready or Failed.
///
/// The per-method protocol flow lives in <see cref="IRequestHandler"/> implementations under
/// <c>Lsp/RequestHandlers/</c> and is dispatched from <see cref="HandleClientAsync"/> via
/// <see cref="Handlers"/>; this class owns only the daemon lifecycle, the socket accept loop,
/// and the readiness state machine.
/// </summary>
public sealed class LspDaemon
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(120);

    private readonly string _workspaceRoot;
    private readonly ILanguageBackend _backend;
    private readonly string _logPath;
    private readonly DocumentSync _docSync;

    // State machine
    private volatile DaemonState _state = DaemonState.Loading;
    private volatile string? _failReason;
    private readonly TaskCompletionSource<bool> _readyTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Set for the duration of RunAsync. A client's "stop" request calls
    // DaemonHost.RequestShutdown() on it, which unwinds the host's accept loop and runs its
    // cleanup (kills the backend child, removes the socket/pid files).
    private DaemonHost? _host;

    // Method-name → handler. Each handler implements IRequestHandler and owns one method's
    // protocol flow end-to-end; the daemon is the dispatcher and the socket/serialization
    // surface only. Handlers are stateless singletons.
    private static readonly IReadOnlyDictionary<string, IRequestHandler> Handlers =
        new Dictionary<string, IRequestHandler>(StringComparer.Ordinal)
        {
            ["refs"] = new RefsHandler(),
            ["def"] = new DefHandler(),
            ["impl"] = new ImplHandler(),
            ["callers"] = new CallersHandler(),
            ["calls"] = new CallsHandler(),
            ["sig"] = new SigHandler(),
            ["sym"] = new SymHandler(),
            ["symbols"] = new SymbolsHandler(),
            ["fix"] = new FixHandler(),
            ["diag"] = new DiagHandler(),
            ["rename"] = new RenameHandler(),
            ["outline"] = new DocumentSymbolHandler(),
        };

    public DaemonState State => _state;

    public LspDaemon(string workspaceRoot, ILanguageBackend backend)
    {
        _workspaceRoot = workspaceRoot;
        _backend = backend;
        _logPath = DaemonSocket.GetLogPath(workspaceRoot);
        _docSync = new DocumentSync(Log);
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
                    () => LspHandshake.HandshakeAsync(
                        loop, _workspaceRoot, combinedHandshake.Token, Log,
                        TransitionToReady,
                        reason => TransitionToFailed(reason),
                        host.ReportFatalStartupFailure),
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
            else if (Handlers.TryGetValue(request.Method, out var handler))
            {
                // The loop is per-process, so capture it once here and hand handlers the
                // (filePath, fileUri, ct) shape DocumentSync already exposes internally —
                // DocumentSync owns the open/resync state, the loop just carries LSP traffic.
                var ctx = new LspDaemonContext(
                    Loop: loop,
                    WaitForReadyAsync: WaitForReadyAsync,
                    EnsureFileOpenAsync: (filePath, fileUri, c) => _docSync.EnsureFileOpenAsync(loop, filePath, fileUri, c),
                    Log: Log);
                response = await handler.HandleAsync(ctx, request, ct).ConfigureAwait(false);
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
