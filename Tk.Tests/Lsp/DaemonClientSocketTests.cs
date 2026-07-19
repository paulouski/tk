using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tk.Lsp;
using Tk.Lsp.Protocol;
using Xunit;

namespace Tk.Tests.Lsp;

/// <summary>
/// Exercises the real unix-domain-socket path <see cref="DaemonClient.SendAsync"/> uses to talk
/// to the LSP daemon, against a scripted FAKE daemon (a bare listener speaking the same
/// line-delimited JSON protocol) — no Roslyn, no real <c>tk __lsp-daemon</c> process. This is the
/// one leg of the daemon protocol <see cref="MessageLoopTests"/> can't reach: that suite drives
/// <see cref="Tk.Lsp.Protocol.MessageLoop"/> over anonymous pipes (the daemon's side talking to
/// its Roslyn child), whereas this exercises <see cref="DaemonClient"/>'s own request/response
/// framing over the actual <see cref="UnixDomainSocketEndPoint"/> transport (the CLI's side
/// talking to the daemon).
///
/// Binds the fake listener at the exact socket path <see cref="DaemonSocket.GetSocketPath"/>
/// derives for the test's workspace root before calling <see cref="DaemonClient.SendAsync"/>, so
/// the client sees the socket already present and skips spawning a real daemon process
/// (mirrors the "socket exists" branch of DaemonClient.SendAsync).
/// </summary>
[Collection("HomeSensitive")]
[Trait("Category", "Integration")]
public class DaemonClientSocketTests
{
    private static string NewWorkspaceRoot() => "/tmp/daemonclient-test-" + Guid.NewGuid();

    private static Socket BindFakeDaemon(string workspaceRoot)
    {
        var socketPath = DaemonSocket.GetSocketPath(workspaceRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        if (File.Exists(socketPath))
            File.Delete(socketPath);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        DaemonSocket.WritePidInfo(
            DaemonSocket.GetPidPath(workspaceRoot),
            new DaemonPidInfo(Environment.ProcessId, null));
        return listener;
    }

    private static void CleanupDaemonState(string workspaceRoot)
    {
        foreach (var path in new[]
                 {
                     DaemonSocket.GetSocketPath(workspaceRoot),
                     DaemonSocket.GetPidPath(workspaceRoot),
                     DaemonSocket.GetSocketPath(workspaceRoot) + ".lock"
                 })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>Accepts exactly one connection, reads one JSON request line, hands it to
    /// <paramref name="respond"/> to build the response object, writes it back as one JSON line.</summary>
    private static async Task<string> RunFakeDaemonOnceAsync(Socket listener, Func<JsonDocument, object> respond)
    {
        using var conn = await listener.AcceptAsync();
        using var stream = new NetworkStream(conn, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

        var requestLine = await reader.ReadLineAsync();
        Assert.NotNull(requestLine);
        using var doc = JsonDocument.Parse(requestLine!);

        var response = respond(doc);
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, LspMessage.Options));
        return requestLine!;
    }

    [Fact]
    public async Task SendAsync_completes_full_roundtrip_against_fake_daemon()
    {
        var root = NewWorkspaceRoot();
        using var listener = BindFakeDaemon(root);

        try
        {
            string? seenMethod = null;
            string? seenFilePath = null;
            var serverTask = RunFakeDaemonOnceAsync(listener, doc =>
            {
                seenMethod = doc.RootElement.GetProperty("method").GetString();
                seenFilePath = doc.RootElement.GetProperty("filePath").GetString();
                return new DaemonResponse(true, null, [new LspLocation("file:///a.cs", 1, 0, 1, 5)]);
            });

            var request = new DaemonRequest("refs", "/a.cs", 1, 0, "Foo");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await DaemonClient.SendAsync(root, request, cts.Token);

            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("refs", seenMethod);
            Assert.Equal("/a.cs", seenFilePath);

            Assert.True(response.Success);
            Assert.Null(response.Error);
            var location = Assert.Single(response.Locations!);
            Assert.Equal("file:///a.cs", location.Uri);
            Assert.Equal(1, location.StartLine);
        }
        finally
        {
            listener.Close();
            CleanupDaemonState(root);
        }
    }

    [Fact]
    public async Task SendAsync_surfaces_a_failure_response_from_fake_daemon()
    {
        var root = NewWorkspaceRoot();
        using var listener = BindFakeDaemon(root);

        try
        {
            var serverTask = RunFakeDaemonOnceAsync(listener,
                _ => new DaemonResponse(false, "symbol 'Bar' not found", null));

            var request = new DaemonRequest("def", "/a.cs", 1, 0, "Bar");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await DaemonClient.SendAsync(root, request, cts.Token);

            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(response.Success);
            Assert.Equal("symbol 'Bar' not found", response.Error);
            Assert.Null(response.Locations);
        }
        finally
        {
            listener.Close();
            CleanupDaemonState(root);
        }
    }

    [Fact]
    public async Task SendAsync_returns_failed_response_when_daemon_closes_connection_without_replying()
    {
        var root = NewWorkspaceRoot();
        using var listener = BindFakeDaemon(root);

        try
        {
            var serverTask = Task.Run(async () =>
            {
                using var conn = await listener.AcceptAsync();
                // Accept, read nothing, close immediately — simulates a daemon that died
                // mid-handshake (e.g. crashed right after accept()).
                await Task.Delay(50);
            });

            var request = new DaemonRequest("refs", "/a.cs", 1, 0, "Foo");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await DaemonClient.SendAsync(root, request, cts.Token);

            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(response.Success);
            Assert.NotNull(response.Error);
        }
        finally
        {
            listener.Close();
            CleanupDaemonState(root);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task SendAsync_replaces_orphaned_state_and_retries_with_fresh_daemon(
        bool withStaleSocket,
        bool withDeadPid)
    {
        var root = NewWorkspaceRoot();
        var socketPath = DaemonSocket.GetSocketPath(root);
        var pidPath = DaemonSocket.GetPidPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        if (withStaleSocket)
            File.WriteAllText(socketPath, "stale socket");
        if (withDeadPid)
            DaemonSocket.WritePidInfo(pidPath, new DaemonPidInfo(int.MaxValue - 1, null));

        Socket? replacementListener = null;
        Task<string>? serverTask = null;
        var spawnCount = 0;

        try
        {
            Task<bool> SpawnFakeDaemon(string workspaceRoot, string _, CancellationToken __)
            {
                spawnCount++;
                replacementListener = BindFakeDaemon(workspaceRoot);
                serverTask = RunFakeDaemonOnceAsync(replacementListener,
                    _ => new DaemonResponse(true, null, [new LspLocation("file:///fresh.cs", 1, 0, 1, 5)]));
                return Task.FromResult(true);
            }

            var request = new DaemonRequest("refs", "/fresh.cs", 1, 0, "Fresh");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await DaemonClient.SendAsync(root, request, cts.Token, SpawnFakeDaemon);

            Assert.NotNull(serverTask);
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, spawnCount);
            Assert.True(response.Success);
            Assert.Equal("file:///fresh.cs", Assert.Single(response.Locations!).Uri);
        }
        finally
        {
            replacementListener?.Close();
            CleanupDaemonState(root);
        }
    }
}
