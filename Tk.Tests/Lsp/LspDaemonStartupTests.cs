using Tk.Lsp;
using Tk.Lsp.Protocol;
using Xunit;

namespace Tk.Tests.Lsp;

/// <summary>
/// Session-level (LspDaemon) startup-failure tests that sit above DaemonHost:
///   f: backend resolution failure -> clean error, no orphan files (ResolveServer runs
///      before DaemonHost is ever touched, so no socket/PID file is created at all).
///   g: early child crash -> the client's WaitForReadyAsync gets a clear, immediate
///      failure (the DaemonHost-side "unwinds promptly" half of this is covered by
///      DaemonHostTests.RunAsync_early_backend_exit_unwinds_and_cleans_via_ReportFatalStartupFailure).
/// </summary>
[Collection("HomeSensitive")]
public class LspDaemonStartupTests
{
    private static string NewWorkspaceRoot() => "/tmp/lspdaemon-startup-test-" + Guid.NewGuid();

    private sealed class NullServerBackend : ILanguageBackend
    {
        public string Name => "stub";
        public string[] FileExtensions => [".cs"];
        public string[] WorkspaceMarkers => [".sln"];
        public string InstallHint => "install it";
        public string? ResolveServer() => null;
        public string[] GetLaunchArgs(string serverPath) => [];
        public bool IsReadySignal(LspIncoming msg) => false;
    }

    private sealed class CrashingBackend : ILanguageBackend
    {
        public string Name => "stub-crash";
        public string[] FileExtensions => [".cs"];
        public string[] WorkspaceMarkers => [".sln"];
        public string InstallHint => "n/a";
        public string? ResolveServer() => "/bin/sh";
        public string[] GetLaunchArgs(string serverPath) => [serverPath, "-c", "exit 7"];
        public bool IsReadySignal(LspIncoming msg) => false;
    }

    // ── (f) backend resolution failure ────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ResolveServer_null_throws_and_creates_no_daemon_files()
    {
        var root = NewWorkspaceRoot();
        var socketPath = DaemonSocket.GetSocketPath(root);
        var pidPath = DaemonSocket.GetPidPath(root);

        var daemon = new LspDaemon(root, new NullServerBackend());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => daemon.RunAsync(CancellationToken.None));

        Assert.Contains("LSP server not found", ex.Message);
        Assert.False(File.Exists(socketPath));
        Assert.False(File.Exists(pidPath));
    }

    // ── (g) early child crash -> client gets a clear failure ─────────────────

    [Fact]
    public async Task RunAsync_early_backend_crash_faults_ReadyTask_and_exits_promptly()
    {
        var root = Directory.CreateTempSubdirectory("lspdaemon-crash-test-").FullName;
        try
        {
            var socketPath = DaemonSocket.GetSocketPath(root);
            var pidPath = DaemonSocket.GetPidPath(root);

            var daemon = new LspDaemon(root, new CrashingBackend());

            // Generous timeouts: the crash itself is near-instant, but under a fully
            // parallel test-suite run the thread pool can be busy enough with other tests'
            // spawned processes that the Process.Exited callback is delayed well past what
            // it'd take in isolation.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var runTask = daemon.RunAsync(cts.Token);

            using var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var ready = await Assert.ThrowsAsync<InvalidOperationException>(
                () => daemon.ReadyTask.WaitAsync(readyCts.Token));
            Assert.Contains("Daemon failed", ready.Message);
            Assert.Equal(DaemonState.Failed, daemon.State);

            // The daemon process itself should exit promptly (ReportFatalStartupFailure),
            // not idle for up to the 30-minute timeout.
            await runTask.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.False(File.Exists(socketPath));
            Assert.False(File.Exists(pidPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
