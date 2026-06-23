using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tk.Lsp;
using Tk.Lsp.Protocol;
using Xunit;

namespace Tk.Tests.Lsp;

/// <summary>
/// Unit tests for the LspDaemon state machine (Loading → Ready / Failed).
/// No real server is spawned — state transitions are driven via internal helpers.
/// </summary>
public class DaemonStateMachineTests
{
    // ── State transition tests ────────────────────────────────────────────────

    [Fact]
    public void Initial_state_is_Loading()
    {
        var daemon = MakeDaemon();
        Assert.Equal(DaemonState.Loading, daemon.State);
    }

    [Fact]
    public void SetReady_transitions_to_Ready()
    {
        var daemon = MakeDaemon();
        daemon.SetReady();
        Assert.Equal(DaemonState.Ready, daemon.State);
    }

    [Fact]
    public void SetFailed_transitions_to_Failed()
    {
        var daemon = MakeDaemon();
        daemon.SetFailed("timeout");
        Assert.Equal(DaemonState.Failed, daemon.State);
    }

    [Fact]
    public async Task ReadyTask_completes_when_SetReady_called()
    {
        var daemon = MakeDaemon();
        var readyTask = daemon.ReadyTask;

        Assert.False(readyTask.IsCompleted);

        daemon.SetReady();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await readyTask.WaitAsync(cts.Token);
        Assert.True(result);
    }

    [Fact]
    public async Task ReadyTask_faults_when_SetFailed_called()
    {
        var daemon = MakeDaemon();
        var readyTask = daemon.ReadyTask;

        daemon.SetFailed("workspace-ready-timeout");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => readyTask.WaitAsync(cts.Token));
        Assert.Contains("workspace-ready-timeout", ex.Message);
    }

    [Fact]
    public async Task ReadyTask_already_complete_for_Ready_state()
    {
        var daemon = MakeDaemon();
        daemon.SetReady();

        // Task should be already completed
        Assert.True(daemon.ReadyTask.IsCompletedSuccessfully);
        var result = await daemon.ReadyTask;
        Assert.True(result);
    }

    [Fact]
    public async Task ReadyTask_already_faulted_for_Failed_state()
    {
        var daemon = MakeDaemon();
        daemon.SetFailed("server-crash");

        Assert.True(daemon.ReadyTask.IsFaulted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => daemon.ReadyTask);
    }

    // ── Client-waits-during-Loading tests ────────────────────────────────────

    [Fact]
    public async Task Multiple_waiters_all_released_on_Ready()
    {
        var daemon = MakeDaemon();

        // Simulate 3 clients waiting during Loading
        var t1 = daemon.ReadyTask;
        var t2 = daemon.ReadyTask;
        var t3 = daemon.ReadyTask;

        // None complete yet
        Assert.False(t1.IsCompleted);
        Assert.False(t2.IsCompleted);
        Assert.False(t3.IsCompleted);

        daemon.SetReady();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(t1, t2, t3).WaitAsync(cts.Token);

        Assert.All(new[] { t1, t2, t3 }, t => Assert.True(t.IsCompletedSuccessfully));
    }

    [Fact]
    public async Task Multiple_waiters_all_faulted_on_Failed()
    {
        var daemon = MakeDaemon();

        var t1 = daemon.ReadyTask;
        var t2 = daemon.ReadyTask;

        daemon.SetFailed("test-failure");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task.WhenAll(t1, t2).WaitAsync(cts.Token));

        Assert.True(t1.IsFaulted);
        Assert.True(t2.IsFaulted);
    }

    // ── DaemonSocket path helpers ─────────────────────────────────────────────

    [Fact]
    public void SocketPath_and_LogPath_share_same_hash()
    {
        const string root = "/workspace/MyProject";
        var socketPath = DaemonSocket.GetSocketPath(root);
        var logPath = DaemonSocket.GetLogPath(root);

        // Both should live in the same directory
        Assert.Equal(Path.GetDirectoryName(socketPath), Path.GetDirectoryName(logPath));

        // They differ only in extension
        Assert.Equal(".sock", Path.GetExtension(socketPath));
        Assert.Equal(".log", Path.GetExtension(logPath));

        // Same base filename (hash)
        Assert.Equal(
            Path.GetFileNameWithoutExtension(socketPath),
            Path.GetFileNameWithoutExtension(logPath));
    }

    [Fact]
    public void SocketPath_is_deterministic()
    {
        const string root = "/workspace/Foo";
        Assert.Equal(DaemonSocket.GetSocketPath(root), DaemonSocket.GetSocketPath(root));
    }

    [Fact]
    public void Different_roots_produce_different_socket_paths()
    {
        var path1 = DaemonSocket.GetSocketPath("/workspace/A");
        var path2 = DaemonSocket.GetSocketPath("/workspace/B");
        Assert.NotEqual(path1, path2);
    }

    [Fact]
    public void SocketPath_lives_in_daemons_dir()
    {
        var path = DaemonSocket.GetSocketPath("/some/root");
        Assert.Contains(Path.Combine(".claude", "tk", "daemons"), path);
    }

    // ── WorkspaceReady predicate correctness ─────────────────────────────────

    [Theory]
    [InlineData("$/progress", "WorkspaceReady", "end", true)]
    [InlineData("$/progress", "WorkspaceReady", "begin", false)]
    [InlineData("$/progress", "WorkspaceReady", "report", false)]
    [InlineData("$/progress", "SomethingElse", "end", false)]
    [InlineData("window/logMessage", "WorkspaceReady", "end", false)]
    public void IsReadySignal_matches_exact_shape(string method, string token, string kind, bool expected)
    {
        var backend = new CSharpBackend();
        var json = method == "$/progress"
            ? "{\"jsonrpc\":\"2.0\",\"method\":\"$/progress\",\"params\":{\"token\":\"" + token + "\",\"value\":{\"kind\":\"" + kind + "\"}}}"
            : "{\"jsonrpc\":\"2.0\",\"method\":\"" + method + "\",\"params\":{}}";

        var msg = LspMessage.Parse(json);
        Assert.Equal(expected, backend.IsReadySignal(msg));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static LspDaemon MakeDaemon() =>
        new("/tmp/test-workspace-" + Guid.NewGuid(), new StubBackend());

    private sealed class StubBackend : ILanguageBackend
    {
        public string Name => "stub";
        public string[] FileExtensions => [".cs"];
        public string[] WorkspaceMarkers => [".sln"];
        public string InstallHint => "n/a";
        public string? ResolveServer() => null;
        public string[] GetLaunchArgs(string serverPath) => [];
        public bool IsReadySignal(LspIncoming msg) => false;
    }
}
