using System.Diagnostics;
using System.Net.Sockets;

namespace Tk.Lsp;

/// <summary>
/// Outcome of a daemon start attempt against a workspace's socket path.
/// </summary>
public enum DaemonStartOutcome
{
    /// <summary>This process bound the socket and ran the daemon to completion.</summary>
    Started,

    /// <summary>
    /// Another process already owns a live socket for this workspace; this process did
    /// not touch any files and should exit quietly.
    /// </summary>
    AlreadyRunningElsewhere,
}

/// <summary>
/// Language-agnostic daemon-process lifecycle host.
///
/// Owns everything about being a long-lived daemon process that has nothing to do with
/// the LSP protocol: acquiring the cross-process single-instance start lock, binding the
/// workspace's unix socket, launching and supervising the backend child process,
/// writing/reading/cleaning up the PID file, detecting and killing orphaned daemons, and
/// graceful-then-forced shutdown (from an explicit stop request, from SIGTERM via the
/// caller's CancellationToken, or from an unrecoverable startup failure).
///
/// Protocol/session behavior — handshake, request dispatch, readiness detection — is
/// supplied by the caller via <see cref="HostOptions"/> and is entirely opaque here: no
/// language IDs, no Roslyn-specific launch flags, no assumptions beyond "one backend
/// child process".
/// </summary>
public sealed class DaemonHost
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LiveProbeTimeout = TimeSpan.FromSeconds(2);

    // How old a start-lock file must be before we'll treat it as abandoned rather than
    // belonging to a concurrent starter still inside the microseconds-wide window between
    // acquiring the lock and completing bind+listen. Generous relative to that window while
    // still short relative to how long a user would wait before retrying after a crash.
    private static readonly TimeSpan StartLockGracePeriod = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _shutdownRequested = new();
    private readonly CancellationTokenSource _fatalStartupFailure = new();

    /// <summary>Requests a graceful shutdown (e.g. in response to a client "stop" request).</summary>
    public void RequestShutdown() => _shutdownRequested.Cancel();

    /// <summary>
    /// Reports that the session can never become ready (backend crashed before readiness,
    /// or the handshake timed out), so the host should unwind and clean up immediately
    /// instead of idling for up to <see cref="IdleTimeout"/> serving a permanently-failed
    /// session.
    /// </summary>
    public void ReportFatalStartupFailure() => _fatalStartupFailure.Cancel();

    /// <summary>
    /// Caller-supplied hooks that give the host everything it needs without knowing any
    /// LSP/Roslyn specifics.
    /// </summary>
    /// <param name="WorkspaceRoot">Workspace root; used only to derive socket/PID paths.</param>
    /// <param name="Log">Sink for host-lifecycle log lines (shares the caller's log file).</param>
    /// <param name="StartBackend">Builds the <see cref="ProcessStartInfo"/> for the single backend child process.</param>
    /// <param name="OnBackendStarted">
    /// Invoked once immediately after the backend process starts. Must return quickly —
    /// any protocol handshake should be kicked off as a background task, not awaited here,
    /// so the host can start accepting clients right away (socket-first design).
    /// </param>
    /// <param name="HandleClient">Per-connection request dispatch; opaque to the host.</param>
    /// <param name="OnBackendExited">
    /// Invoked when the backend process exits, with its exit code. The caller decides
    /// whether this is fatal (e.g. only while still starting up) and, if so, calls
    /// <see cref="ReportFatalStartupFailure"/> itself.
    /// </param>
    public sealed record HostOptions(
        string WorkspaceRoot,
        Action<string> Log,
        Func<ProcessStartInfo> StartBackend,
        Func<Process, CancellationToken, Task> OnBackendStarted,
        Func<Socket, CancellationToken, Task> HandleClient,
        Action<int>? OnBackendExited = null);

    /// <summary>
    /// Acquires the workspace's start lock (standing down if another daemon already owns
    /// it), binds the socket, launches the backend process, persists the PID file, and
    /// serves client connections until stopped, cancelled, or a fatal startup failure is
    /// reported. Cleans up the socket/PID files and kills the backend process tree on the
    /// way out.
    /// </summary>
    public async Task<DaemonStartOutcome> RunAsync(HostOptions options, CancellationToken ct)
    {
        var socketPath = DaemonSocket.GetSocketPath(options.WorkspaceRoot);
        var pidPath = DaemonSocket.GetPidPath(options.WorkspaceRoot);
        var lockPath = socketPath + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);

        if (!TryAcquireStartLock(lockPath, options.Log))
            return DaemonStartOutcome.AlreadyRunningElsewhere;

        // From here on the lock file is held for the daemon's entire lifetime (deleted only
        // in the outermost finally below) — releasing it any earlier would let a
        // late-arriving racer reacquire it and steal this live socket out from under us,
        // exactly the bug this lock exists to prevent.
        try
        {
            // We now exclusively own starting the daemon for this workspace (the lock file
            // on disk is proof), so any leftover socket file here is unambiguously stale
            // (left by a crash) and safe to remove before rebinding.
            if (File.Exists(socketPath))
                File.Delete(socketPath);

            using var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            server.Bind(new UnixDomainSocketEndPoint(socketPath));
            server.Listen(8);
            options.Log($"socket bound: {socketPath}");

            using var process = new Process { StartInfo = options.StartBackend() };
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                int code;
                try { code = process.ExitCode; } catch { code = -1; }
                options.Log($"server process EXITED code={code}");
                options.OnBackendExited?.Invoke(code);
            };

            process.Start();
            options.Log($"server process launched, pid={process.Id}");

            // Persist both PIDs so `lsp stop`/`lsp status` can verify and, if necessary,
            // forcibly terminate this daemon and its backend child even if the socket-based
            // graceful stop is unresponsive or the socket file itself is gone.
            DaemonSocket.WritePidInfo(pidPath, new DaemonPidInfo(Environment.ProcessId, process.Id));

            // Pump backend stderr into the daemon log — the one channel that shows
            // MSBuild/SDK/BuildHost failures for the Roslyn backend.
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) options.Log($"[stderr] {e.Data}");
            };
            process.BeginErrorReadLine();

            // Socket-first: clients can already start queueing on the socket while this
            // runs. The caller must not await a full handshake here — only kick it off in
            // the background — or client connections would stall behind it needlessly.
            await options.OnBackendStarted(process, ct).ConfigureAwait(false);

            using var idleTimer = new CancellationTokenSource(IdleTimeout);
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(
                ct, idleTimer.Token, _shutdownRequested.Token, _fatalStartupFailure.Token);

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

                    options.Log("client connected");
                    idleTimer.CancelAfter(IdleTimeout);
                    _ = Task.Run(async () =>
                    {
                        try { await options.HandleClient(client, combined.Token).ConfigureAwait(false); }
                        catch (Exception ex) { options.Log($"client handler error: {ex}"); }
                        finally { client.Dispose(); }
                    }, combined.Token);
                }
            }
            finally
            {
                if (File.Exists(socketPath))
                    File.Delete(socketPath);
                if (File.Exists(pidPath))
                    try { File.Delete(pidPath); } catch { }
                try { process.Kill(entireProcessTree: true); } catch { }
                options.Log("daemon stopped");
            }
        }
        finally
        {
            TryDelete(lockPath);
        }

        return DaemonStartOutcome.Started;
    }

    /// <summary>
    /// Acquires the cross-process single-instance lock for a workspace by exclusively
    /// creating <paramref name="lockPath"/> (<see cref="FileMode.CreateNew"/>, which maps to
    /// an atomic O_CREAT|O_EXCL open — no TOCTOU window, unlike binding-then-probing the
    /// socket itself: an earlier version of this method used the socket bind as the lock and
    /// probed liveness on failure, but that left a real race between a winner's successful
    /// bind and its subsequent listen — a loser's probe in that gap saw "exists but not
    /// live" and wrongly stole the socket file out from under the winner).
    ///
    /// On failure the lock file already exists. Its age decides whether that's a legitimate
    /// concurrent/active owner (young — plausibly still inside the microseconds-wide
    /// bind-then-listen window, or genuinely running) or an abandoned lock from a daemon that
    /// crashed before it could delete the file (older than <see cref="StartLockGracePeriod"/>
    /// — safe to reclaim and retry once).
    /// </summary>
    private static bool TryAcquireStartLock(string lockPath, Action<string> log)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var _ = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                var age = TryGetAge(lockPath);
                if (age is null || age < StartLockGracePeriod)
                {
                    log($"start lock held at {lockPath} — another daemon is starting/running for this workspace, standing down");
                    return false;
                }

                log($"stale start lock at {lockPath} (age={age}), removing and retrying");
                TryDelete(lockPath);
            }
        }

        return false;
    }

    private static TimeSpan? TryGetAge(string path)
    {
        try { return File.Exists(path) ? DateTime.UtcNow - File.GetCreationTimeUtc(path) : null; }
        catch { return null; }
    }

    /// <summary>
    /// True if a unix socket at <paramref name="socketPath"/> both exists and has a live
    /// listener that accepts a connection. A socket file can outlive its process (e.g.
    /// after a SIGKILL that skipped cleanup), so a bare <see cref="File.Exists(string)"/>
    /// check is not sufficient proof.
    /// </summary>
    public static async Task<bool> IsSocketLiveAsync(string socketPath, CancellationToken ct)
    {
        if (!File.Exists(socketPath))
            return false;

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var timeoutCts = new CancellationTokenSource(LiveProbeTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), linked.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Cleans up orphaned daemon state for a workspace whose socket is not live: kills any
    /// process still alive per the PID file (daemon and/or backend), then removes the PID
    /// file, any stale socket file, and any stale start-lock file (a `kill -9` skips
    /// DaemonHost's own cleanup just as it does for the socket/PID files). Returns true if a
    /// live orphan process was found and killed (as opposed to only stale files being
    /// removed).
    /// </summary>
    public static bool CleanupOrphan(string pidPath, string socketPath)
    {
        var pidInfo = DaemonSocket.TryReadPidInfo(pidPath);
        var orphanKilled = pidInfo is not null && TryForceKill(pidInfo);

        TryDelete(pidPath);
        TryDelete(socketPath);
        TryDelete(socketPath + ".lock");
        return orphanKilled;
    }

    /// <summary>
    /// Force-kills the daemon and/or backend process named in <paramref name="pidInfo"/> if
    /// still alive. Returns true if anything was actually killed.
    /// </summary>
    public static bool TryForceKill(DaemonPidInfo pidInfo)
    {
        var killed = false;
        if (DaemonSocket.IsProcessAlive(pidInfo.DaemonPid))
        {
            killed = true;
            DaemonSocket.TryKillProcessTree(pidInfo.DaemonPid);
        }
        if (pidInfo.ServerPid is int serverPid && DaemonSocket.IsProcessAlive(serverPid))
        {
            killed = true;
            DaemonSocket.TryKillProcessTree(serverPid);
        }
        return killed;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
