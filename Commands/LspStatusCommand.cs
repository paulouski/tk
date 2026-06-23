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
            "status" => RunStatus(ctx),
            "stop" => await RunStopAsync(ctx).ConfigureAwait(false),
            _ => Usage(ctx)
        };
    }

    private static int RunStatus(CommandContext ctx)
    {
        var workspaceRoot = ResolveWorkspaceRoot();
        if (workspaceRoot is null)
        {
            ctx.Out.WriteLine("lsp status=stopped workspace=unknown");
            return 0;
        }

        var socketPath = DaemonSocket.GetSocketPath(workspaceRoot);
        if (File.Exists(socketPath))
        {
            ctx.Out.WriteLine($"lsp status=running workspace={workspaceRoot}");
            ctx.Out.WriteLine($"  socket={socketPath}");
        }
        else
        {
            ctx.Out.WriteLine($"lsp status=stopped workspace={workspaceRoot}");
        }

        return 0;
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
        if (!File.Exists(socketPath))
        {
            ctx.Out.WriteLine("lsp stop: daemon not running");
            return 0;
        }

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cts.Token).ConfigureAwait(false);

            using var stream = new NetworkStream(socket, ownsSocket: false);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            var stopRequest = new DaemonRequest("stop", null, 0, 0, null);
            await writer.WriteLineAsync(JsonSerializer.Serialize(stopRequest, LspMessage.Options)).ConfigureAwait(false);
        }
        catch
        {
            // If connect fails, just delete the socket file
        }

        if (File.Exists(socketPath))
            File.Delete(socketPath);

        ctx.Out.WriteLine("lsp stop: daemon stopped");
        return 0;
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
