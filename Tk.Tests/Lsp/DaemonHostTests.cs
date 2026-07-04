using System.Diagnostics;
using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

/// <summary>
/// Lifecycle tests for the language-agnostic <see cref="DaemonHost"/>, extracted from
/// LspDaemon in architecture phase C. No real LSP server is involved — a plain
/// <c>/bin/sh</c> child process stands in for "the backend" wherever a real child process
/// is needed, since the host is deliberately opaque to what the backend actually is.
///
/// Letter references are to the approved lifecycle-test list for phase C:
///   a: stale socket file + no process -> cleaned, not reported as running
///   b: stale PID file + dead PID -> cleaned
///   c: live PID + missing socket -> orphan killed and cleaned
///   d: stop kills daemon AND child process tree
///   e: cancelling the host's token (stand-in for SIGTERM) triggers the same cleanup as stop
///   g: early child crash -> host unwinds and cleans up immediately (does not idle out)
///   h: a fatal startup failure reported by the caller (e.g. handshake timeout) unwinds the
///      host the same way as (g)/stop -- exercises the same ReportFatalStartupFailure plumbing
///   i: client-connect-while-loading is unaffected by this extraction; already covered by
///      DaemonStateMachineTests' waiter tests (Multiple_waiters_all_released_on_Ready /
///      _all_faulted_on_Failed) which test the exact same WaitForReadyAsync/_readyTcs logic
///      that still lives in LspDaemon, unmoved.
///   j: two concurrent starts for the same workspace -> exactly one daemon, one socket, no orphan
///
/// (f) "backend resolution failure -> clean error, no orphan files" is a session-level concern
/// (LspDaemon calls ILanguageBackend.ResolveServer() before ever touching DaemonHost) and is
/// covered in DaemonStateMachineTests / LspDaemonStartupTests instead.
/// </summary>
[Collection("HomeSensitive")]
public class DaemonHostTests
{
    private static string NewWorkspaceRoot() => "/tmp/daemonhost-test-" + Guid.NewGuid();

    private static ProcessStartInfo SleepBackend(int seconds = 30) => new()
    {
        FileName = "/bin/sh",
        ArgumentList = { "-c", $"sleep {seconds}" },
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    private static DaemonHost.HostOptions MakeOptions(
        string workspaceRoot,
        TaskCompletionSource<Process>? started = null,
        Func<ProcessStartInfo>? startBackend = null,
        Action<int>? onBackendExited = null) => new(
        WorkspaceRoot: workspaceRoot,
        Log: _ => { },
        StartBackend: startBackend ?? (() => SleepBackend()),
        OnBackendStarted: (process, ct) =>
        {
            started?.TrySetResult(process);
            return Task.CompletedTask;
        },
        HandleClient: (_, _) => Task.CompletedTask,
        OnBackendExited: onBackendExited);

    // ── (a) stale socket file + no process ────────────────────────────────────

    [Fact]
    public void CleanupOrphan_stale_socket_no_pidfile_removes_socket_file()
    {
        var root = NewWorkspaceRoot();
        var socketPath = DaemonSocket.GetSocketPath(root);
        var pidPath = DaemonSocket.GetPidPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        File.WriteAllText(socketPath, "stale, not a real socket");

        var killed = DaemonHost.CleanupOrphan(pidPath, socketPath);

        Assert.False(killed);
        Assert.False(File.Exists(socketPath));
        Assert.False(File.Exists(pidPath));
    }

    // A `kill -9` on the daemon (e.g. letter c's live-PID-missing-socket scenario, or a kill
    // mid-startup) skips DaemonHost's own finally-block cleanup just as it does for the
    // socket/PID files, so the start-lock file can be left behind too; CleanupOrphan must
    // remove it as well or every future start for the workspace stands down forever.
    [Fact]
    public void CleanupOrphan_also_removes_leftover_start_lock_file()
    {
        var root = NewWorkspaceRoot();
        var socketPath = DaemonSocket.GetSocketPath(root);
        var pidPath = DaemonSocket.GetPidPath(root);
        var lockPath = socketPath + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        File.WriteAllText(lockPath, "");

        DaemonHost.CleanupOrphan(pidPath, socketPath);

        Assert.False(File.Exists(lockPath));
    }

    // ── (b) stale PID file + dead PID ─────────────────────────────────────────

    [Fact]
    public void CleanupOrphan_stale_pidfile_dead_pid_removes_pidfile()
    {
        var root = NewWorkspaceRoot();
        var socketPath = DaemonSocket.GetSocketPath(root);
        var pidPath = DaemonSocket.GetPidPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(pidPath)!);
        DaemonSocket.WritePidInfo(pidPath, new DaemonPidInfo(int.MaxValue - 1, null));

        var killed = DaemonHost.CleanupOrphan(pidPath, socketPath);

        Assert.False(killed);
        Assert.False(File.Exists(pidPath));
    }

    // ── (c) live PID + missing socket ─────────────────────────────────────────

    [Fact]
    public async Task CleanupOrphan_live_pid_missing_socket_kills_and_cleans()
    {
        var root = NewWorkspaceRoot();
        var socketPath = DaemonSocket.GetSocketPath(root); // never created
        var pidPath = DaemonSocket.GetPidPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(pidPath)!);

        using var proc = new Process { StartInfo = SleepBackend() };
        proc.Start();
        DaemonSocket.WritePidInfo(pidPath, new DaemonPidInfo(proc.Id, null));

        var killed = DaemonHost.CleanupOrphan(pidPath, socketPath);

        Assert.True(killed);
        Assert.False(File.Exists(pidPath));
        Assert.False(File.Exists(socketPath));

        await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.True(proc.HasExited);
    }

    // ── (d) stop kills daemon AND child process tree ─────────────────────────

    [Fact]
    public async Task RunAsync_RequestShutdown_kills_backend_process_and_cleans_files()
    {
        var root = NewWorkspaceRoot();
        var socketPath = DaemonSocket.GetSocketPath(root);
        var pidPath = DaemonSocket.GetPidPath(root);
        var started = new TaskCompletionSource<Process>();
        var host = new DaemonHost();

        using var cts = new CancellationTokenSource();
        var runTask = host.RunAsync(MakeOptions(root, started), cts.Token);

        var backend = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var backendPid = backend.Id; // capture before the host disposes the Process object
        Assert.True(File.Exists(socketPath));
        Assert.True(File.Exists(pidPath));

        host.RequestShutdown();
        var outcome = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DaemonStartOutcome.Started, outcome);
        Assert.False(File.Exists(socketPath));
        Assert.False(File.Exists(pidPath));

        // The host disposes its Process object before RunAsync returns, so HasExited/Id are
        // no longer readable on it here — check liveness by the PID captured above instead.
        await Task.Delay(200);
        Assert.False(DaemonSocket.IsProcessAlive(backendPid));
    }

    // ── (e) cancelling the caller token (stand-in for SIGTERM) -> same cleanup as stop ──

    [Fact]
    public async Task RunAsync_token_cancellation_triggers_same_cleanup_as_stop()
    {
        var root = NewWorkspaceRoot();
        var socketPath = DaemonSocket.GetSocketPath(root);
        var pidPath = DaemonSocket.GetPidPath(root);
        var started = new TaskCompletionSource<Process>();
        var host = new DaemonHost();

        using var cts = new CancellationTokenSource();
        var runTask = host.RunAsync(MakeOptions(root, started), cts.Token);

        var backend = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var backendPid = backend.Id;
        Assert.True(File.Exists(socketPath));

        cts.Cancel(); // LspDaemonCommand routes SIGTERM through this same token
        var outcome = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DaemonStartOutcome.Started, outcome);
        Assert.False(File.Exists(socketPath));
        Assert.False(File.Exists(pidPath));
        await Task.Delay(200);
        Assert.False(DaemonSocket.IsProcessAlive(backendPid));
    }

    // ── (g) early child crash -> host unwinds immediately, files cleaned ─────

    [Fact]
    public async Task RunAsync_early_backend_exit_unwinds_and_cleans_via_ReportFatalStartupFailure()
    {
        var root = NewWorkspaceRoot();
        var socketPath = DaemonSocket.GetSocketPath(root);
        var pidPath = DaemonSocket.GetPidPath(root);
        var host = new DaemonHost();

        var crashingBackend = () => new ProcessStartInfo
        {
            FileName = "/bin/sh",
            ArgumentList = { "-c", "exit 7" },
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Mirrors how LspDaemon wires OnBackendExited: report fatal only while still starting.
        var options = MakeOptions(
            root,
            startBackend: crashingBackend,
            onBackendExited: _ => host.ReportFatalStartupFailure());

        using var cts = new CancellationTokenSource();
        var outcome = await host.RunAsync(options, cts.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DaemonStartOutcome.Started, outcome);
        Assert.False(File.Exists(socketPath));
        Assert.False(File.Exists(pidPath));
    }

    // ── (h) a caller-reported fatal startup failure (e.g. handshake timeout) behaves the
    //        same as (g)/stop: same plumbing, different trigger ─────────────────────────

    [Fact]
    public async Task RunAsync_ReportFatalStartupFailure_unwinds_same_as_stop()
    {
        var root = NewWorkspaceRoot();
        var socketPath = DaemonSocket.GetSocketPath(root);
        var pidPath = DaemonSocket.GetPidPath(root);
        var started = new TaskCompletionSource<Process>();
        var host = new DaemonHost();

        using var cts = new CancellationTokenSource();
        var runTask = host.RunAsync(MakeOptions(root, started), cts.Token);

        var backend = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var backendPid = backend.Id;
        Assert.True(File.Exists(socketPath));

        // Simulates a session-side handshake timeout calling the same method LspDaemon wires
        // HandshakeAsync's catch blocks to.
        host.ReportFatalStartupFailure();
        var outcome = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DaemonStartOutcome.Started, outcome);
        Assert.False(File.Exists(socketPath));
        Assert.False(File.Exists(pidPath));
        await Task.Delay(200);
        Assert.False(DaemonSocket.IsProcessAlive(backendPid));
    }

    // ── (j) concurrent startup race -> exactly one daemon, one socket, no orphan ─────────

    [Fact]
    public async Task RunAsync_concurrent_start_race_exactly_one_daemon_wins()
    {
        var root = NewWorkspaceRoot();
        var socketPath = DaemonSocket.GetSocketPath(root);
        var pidPath = DaemonSocket.GetPidPath(root);

        var host1 = new DaemonHost();
        var host2 = new DaemonHost();
        var started1 = new TaskCompletionSource<Process>();
        var started2 = new TaskCompletionSource<Process>();

        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();

        // Fire both starts back-to-back with no await between them so they genuinely race
        // the underlying bind() syscall for the same socket path.
        var task1 = host1.RunAsync(MakeOptions(root, started1), cts1.Token);
        var task2 = host2.RunAsync(MakeOptions(root, started2), cts2.Token);

        // Exactly one of the two RunAsync calls returns quickly (the loser, standing down
        // without starting a backend); the winner keeps its accept loop open indefinitely.
        var firstDone = await Task.WhenAny(task1, task2).WaitAsync(TimeSpan.FromSeconds(10));
        var loserOutcome = await firstDone;
        Assert.Equal(DaemonStartOutcome.AlreadyRunningElsewhere, loserOutcome);

        var (winnerHost, winnerTask, winnerStarted) = firstDone == task1
            ? (host2, task2, started2)
            : (host1, task1, started1);
        var loserStarted = firstDone == task1 ? started1 : started2;

        // The loser must never have reached StartBackend.
        Assert.False(loserStarted.Task.IsCompleted);

        var winnerBackend = await winnerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var winnerBackendPid = winnerBackend.Id;
        Assert.True(File.Exists(socketPath));
        Assert.True(File.Exists(pidPath));

        winnerHost.RequestShutdown();
        var winnerOutcome = await winnerTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DaemonStartOutcome.Started, winnerOutcome);
        Assert.False(File.Exists(socketPath));
        Assert.False(File.Exists(pidPath));
        await Task.Delay(200);
        Assert.False(DaemonSocket.IsProcessAlive(winnerBackendPid));
    }
}
