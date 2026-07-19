using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tk.Lsp.Protocol;

namespace Tk.Lsp;

/// <summary>
/// Shared helper that ensures the LSP daemon is available, recovers orphaned socket/PID
/// state, connects to its unix socket, sends a single request, and reads one response.
/// </summary>
public static class DaemonClient
{
    /// <summary>
    /// Spawns the daemon if absent, replaces orphaned daemon state, then connects (5s
    /// connect-retry loop), sends the request line, and reads one response line. A connect
    /// failure triggers one clean restart before any request is sent. On spawn failure or
    /// null response, returns a failed DaemonResponse.
    /// </summary>
    public static async Task<DaemonResponse> SendAsync(
        string workspaceRoot, DaemonRequest request, CancellationToken ct)
        => await SendAsync(workspaceRoot, request, ct, SpawnDaemonAsync).ConfigureAwait(false);

    internal static async Task<DaemonResponse> SendAsync(
        string workspaceRoot,
        DaemonRequest request,
        CancellationToken ct,
        Func<string, string, CancellationToken, Task<bool>> spawnDaemon)
    {
        var socketPath = DaemonSocket.GetSocketPath(workspaceRoot);
        var pidPath = DaemonSocket.GetPidPath(workspaceRoot);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!await EnsureDaemonAvailableAsync(
                    workspaceRoot, socketPath, pidPath, ct, spawnDaemon).ConfigureAwait(false))
                return new DaemonResponse(false, "failed to spawn daemon", null);

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

            try
            {
                // Retry connect briefly in case there is a tiny race between socket creation
                // and the client reaching this point (normal: socket exists within ~100ms of spawn).
                var connectDeadline = DateTime.UtcNow.AddSeconds(5);
                while (true)
                {
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
                        break;
                    }
                    catch (SocketException) when (DateTime.UtcNow < connectDeadline && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(200, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (SocketException) when (attempt == 0 && !ct.IsCancellationRequested)
            {
                // The daemon may have died after the availability check but before connect.
                // Remove its orphaned state and retry one clean spawn; never retry a request
                // after it has been sent because mutation commands are not idempotent.
                DaemonHost.CleanupOrphan(pidPath, socketPath);
                continue;
            }

            try
            {
                using var stream = new NetworkStream(socket, ownsSocket: false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                await writer.WriteLineAsync(JsonSerializer.Serialize(request, LspMessage.Options)).ConfigureAwait(false);
                var responseJson = await reader.ReadLineAsync(ct).ConfigureAwait(false);

                if (responseJson is null)
                    return new DaemonResponse(false, "daemon returned empty response", null);

                var response = JsonSerializer.Deserialize<DaemonResponse>(responseJson, LspMessage.Options);
                return response ?? new DaemonResponse(false, "daemon returned null response", null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new DaemonResponse(false, ex.Message, null);
            }
        }

        return new DaemonResponse(false, "failed to connect to daemon after restart", null);
    }

    private static async Task<bool> EnsureDaemonAvailableAsync(
        string workspaceRoot,
        string socketPath,
        string pidPath,
        CancellationToken ct,
        Func<string, string, CancellationToken, Task<bool>> spawnDaemon)
    {
        if (File.Exists(socketPath))
        {
            var pidInfo = DaemonSocket.TryReadPidInfo(pidPath);
            if (pidInfo is not null && DaemonSocket.IsProcessAlive(pidInfo.DaemonPid))
                return true;

            // A live socket with no PID file (or the previous daemon's stale PID file) can
            // exist during the short bind-to-PID-write startup window. Probe it before
            // deleting anything so concurrent starts remain safe.
            if (await DaemonHost.IsSocketLiveAsync(socketPath, ct).ConfigureAwait(false))
                return true;

            DaemonHost.CleanupOrphan(pidPath, socketPath);
        }
        else if (DaemonSocket.TryReadPidInfo(pidPath) is not null)
        {
            // A live/stale process without its socket cannot serve clients. Reap it before
            // starting a replacement so its backend and start lock cannot block recovery.
            DaemonHost.CleanupOrphan(pidPath, socketPath);
        }

        return await spawnDaemon(workspaceRoot, socketPath, ct).ConfigureAwait(false);
    }

    private static async Task<bool> SpawnDaemonAsync(string workspaceRoot, string socketPath, CancellationToken ct)
    {
        try
        {
            var tkPath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "tk";
            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tkPath,
                    ArgumentList = { "__lsp-daemon", workspaceRoot },
                    UseShellExecute = false,
                    // Detach the daemon's stdio from the caller's inherited descriptors.
                    // With these false the long-lived daemon inherits the caller's stdout/stderr,
                    // so `tk refs ... | grep` or `$(tk refs ...)` would hang: the daemon holds the
                    // pipe's write end open and the consumer never sees EOF until the daemon idles
                    // out (~30 min). Redirecting gives the daemon its own pipes (owned by the
                    // short-lived parent), which close when the parent exits. The daemon writes
                    // only to its log file on the normal path, so the undrained pipes never fill.
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };
            proc.Start();

            // Socket-first: the daemon binds the socket before any server interaction,
            // so it should appear within ~500ms of process start.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            while (!File.Exists(socketPath) && !linked.Token.IsCancellationRequested)
                await Task.Delay(100, linked.Token).ConfigureAwait(false);

            return File.Exists(socketPath);
        }
        catch
        {
            return false;
        }
    }
}
