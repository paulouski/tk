using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tk.Lsp;
using Tk.Lsp.Protocol;

namespace Tk.Commands;

public sealed class LspStatusCommand : ICommand
{
    public string Name => "lsp";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        var sub = ctx.Args.Length > 0 ? ctx.Args[0] : "status";

        return sub switch
        {
            "status" => await RunStatusAsync(ctx).ConfigureAwait(false),
            "stop" => await RunStopAsync(ctx).ConfigureAwait(false),
            _ => Usage(ctx)
        };
    }

    private static async Task<int> RunStatusAsync(CommandContext ctx)
    {
        var workspaceRoot = ResolveWorkspaceRoot();
        if (workspaceRoot is null)
        {
            ctx.Out.WriteLine("lsp status=stopped workspace=unknown");
            return 0;
        }

        var socketPath = DaemonSocket.GetSocketPath(workspaceRoot);
        // A socket FILE can outlive its listener (e.g. the daemon was killed -9 before it
        // could clean up), so File.Exists alone is not proof the daemon is actually running —
        // probe it with a real connect.
        if (await IsSocketLiveAsync(socketPath).ConfigureAwait(false))
        {
            ctx.Out.WriteLine($"lsp status=running workspace={workspaceRoot}");
            ctx.Out.WriteLine($"  socket={socketPath}");
            return 0;
        }

        // The socket is dead (missing, or a stale file left by a crash) but the daemon
        // (and/or its Roslyn child) may still be an orphan — clean it up instead of leaving
        // it to accumulate, and instead of reporting "stopped" while it is in fact still alive.
        var pidPath = DaemonSocket.GetPidPath(workspaceRoot);
        var pidInfo = DaemonSocket.TryReadPidInfo(pidPath);
        if (pidInfo is not null)
        {
            var daemonAlive = DaemonSocket.IsProcessAlive(pidInfo.DaemonPid);
            var serverAlive = pidInfo.ServerPid is int sp && DaemonSocket.IsProcessAlive(sp);
            if (daemonAlive || serverAlive)
            {
                if (daemonAlive) DaemonSocket.TryKillProcessTree(pidInfo.DaemonPid);
                if (serverAlive) DaemonSocket.TryKillProcessTree(pidInfo.ServerPid!.Value);
                TryDelete(pidPath);
                TryDelete(socketPath);
                ctx.Out.WriteLine($"lsp status=stopped workspace={workspaceRoot} (cleaned up orphaned daemon process)");
                return 0;
            }
            TryDelete(pidPath);
        }

        TryDelete(socketPath); // stale file with no live listener behind it
        ctx.Out.WriteLine($"lsp status=stopped workspace={workspaceRoot}");
        return 0;
    }

    /// <summary>
    /// True if a unix socket at <paramref name="socketPath"/> both exists and has a live
    /// listener that accepts a connection. A socket file can outlive its process (e.g. after
    /// a SIGKILL that skipped cleanup), so a bare File.Exists check is not sufficient proof.
    /// </summary>
    private static async Task<bool> IsSocketLiveAsync(string socketPath)
    {
        if (!File.Exists(socketPath))
            return false;

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> RunStopAsync(CommandContext ctx)
    {
        var workspaceRoot = ResolveWorkspaceRoot();
        if (workspaceRoot is null)
        {
            ctx.Err.WriteLine("lsp stop: could not find workspace root");
            return 1;
        }

        var socketPath = DaemonSocket.GetSocketPath(workspaceRoot);
        var pidPath = DaemonSocket.GetPidPath(workspaceRoot);
        var pidInfo = DaemonSocket.TryReadPidInfo(pidPath);
        var socketExists = File.Exists(socketPath);

        if (!socketExists && pidInfo is null)
        {
            ctx.Out.WriteLine("lsp stop: daemon not running");
            return 0;
        }

        // Ask the daemon to shut down gracefully — it kills its own Roslyn child and removes
        // its socket/pid files as part of that (see LspDaemon's "stop" handler).
        var gracefulSent = false;
        if (socketExists)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), connectCts.Token).ConfigureAwait(false);

                using var stream = new NetworkStream(socket, ownsSocket: false);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var stopRequest = new DaemonRequest("stop", null, 0, 0, null);
                await writer.WriteLineAsync(JsonSerializer.Serialize(stopRequest, LspMessage.Options)).ConfigureAwait(false);
                await reader.ReadLineAsync(connectCts.Token).ConfigureAwait(false); // best-effort ack
                gracefulSent = true;
            }
            catch
            {
                // Socket dead or unresponsive — fall through to the PID-based fallback below.
            }
        }

        // Verify the daemon actually exited; if not (unresponsive, or this was a stale-orphan
        // case with no live socket at all), force-kill by PID. This is what makes `lsp stop`
        // reliable even when the graceful path fails.
        var forceKilled = false;
        if (pidInfo is not null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(gracefulSent ? 3 : 0);
            while (DateTime.UtcNow < deadline && DaemonSocket.IsProcessAlive(pidInfo.DaemonPid))
                await Task.Delay(100).ConfigureAwait(false);

            if (DaemonSocket.IsProcessAlive(pidInfo.DaemonPid))
            {
                forceKilled = true;
                DaemonSocket.TryKillProcessTree(pidInfo.DaemonPid);
            }
            if (pidInfo.ServerPid is int serverPid && DaemonSocket.IsProcessAlive(serverPid))
            {
                forceKilled = true;
                DaemonSocket.TryKillProcessTree(serverPid);
            }
        }

        TryDelete(socketPath);
        TryDelete(pidPath);

        ctx.Out.WriteLine(forceKilled
            ? "lsp stop: daemon stopped (force-killed unresponsive/orphaned process)"
            : "lsp stop: daemon stopped");
        return 0;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static int Usage(CommandContext ctx)
    {
        ctx.Out.WriteLine("usage: tk lsp status|stop");
        return 0;
    }

    private static string? ResolveWorkspaceRoot()
    {
        var target = Common.DotnetWorkspaceResolver.FindTarget(Directory.GetCurrentDirectory());
        if (target is null)
            return null;
        return Path.GetDirectoryName(Path.GetFullPath(target));
    }
}
