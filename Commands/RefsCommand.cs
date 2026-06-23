using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tk.Lsp;
using Tk.Lsp.Protocol;

namespace Tk.Commands;

public sealed class RefsCommand : ICommand
{
    public string Name => "refs";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        if (ctx.Args.Length == 0)
        {
            ctx.Out.WriteLine("usage: tk refs <symbol>");
            ctx.Out.WriteLine("       tk refs <file:line:col>");
            return 1;
        }

        var arg = ctx.Args[0];

        // Try to resolve workspace root
        var workspaceRoot = ResolveWorkspaceRoot();
        if (workspaceRoot is null)
        {
            ctx.Err.WriteLine("tk refs: could not find workspace root (.sln or .csproj)");
            return 1;
        }

        var socketPath = DaemonSocket.GetSocketPath(workspaceRoot);

        // Spawn daemon if not running (socket-first: socket appears within milliseconds of daemon start)
        if (!File.Exists(socketPath))
        {
            var spawnResult = await SpawnDaemonAsync(workspaceRoot, ctx).ConfigureAwait(false);
            if (!spawnResult)
                return 1;
        }

        // Parse position or symbol
        DaemonRequest request;
        if (TryParsePosition(arg, out var filePath, out var line, out var col))
        {
            // The daemon builds a file:// URI from this path, which requires an absolute path.
            request = new DaemonRequest("refs", Path.GetFullPath(filePath), line, col, null);
        }
        else
        {
            request = new DaemonRequest("refs", null, 0, 0, arg);
        }

        // Connect and send request (120s total — daemon may still be doing cold workspace load)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

            // Retry connect briefly in case there is a tiny race between socket creation
            // and the client reaching this point (normal: socket exists within ~100ms of spawn)
            var connectDeadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cts.Token).ConfigureAwait(false);
                    break;
                }
                catch (SocketException) when (DateTime.UtcNow < connectDeadline && !cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(200, cts.Token).ConfigureAwait(false);
                }
            }

            using var stream = new NetworkStream(socket, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            await writer.WriteLineAsync(JsonSerializer.Serialize(request, LspMessage.Options)).ConfigureAwait(false);
            var responseJson = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);

            if (responseJson is null)
            {
                ctx.Err.WriteLine("tk refs: daemon returned empty response");
                return 1;
            }

            var response = JsonSerializer.Deserialize<DaemonResponse>(responseJson, LspMessage.Options);
            if (response is null || !response.Success)
            {
                ctx.Err.WriteLine($"tk refs: {response?.Error ?? "unknown error"}");
                return 1;
            }

            var symbol = arg;
            var locations = response.Locations ?? [];
            ctx.Out.WriteLine(RefsFormatter.Format(symbol, locations));
            return 0;
        }
        catch (OperationCanceledException)
        {
            ctx.Err.WriteLine("tk refs: timed out waiting for daemon response");
            return 1;
        }
        catch (Exception ex)
        {
            ctx.Err.WriteLine($"tk refs: {ex.Message}");
            return 1;
        }
    }

    private static bool TryParsePosition(string arg, out string filePath, out int line, out int col)
    {
        filePath = "";
        line = 0;
        col = 0;

        // Format: file:line:col (e.g. /path/to/File.cs:10:5)
        var lastColon = arg.LastIndexOf(':');
        if (lastColon <= 0) return false;

        var beforeLastColon = arg[..lastColon];
        var secondLastColon = beforeLastColon.LastIndexOf(':');
        if (secondLastColon <= 0) return false;

        var fileCandidate = arg[..secondLastColon];
        var lineStr = arg[(secondLastColon + 1)..lastColon];
        var colStr = arg[(lastColon + 1)..];

        if (!int.TryParse(lineStr, out var l) || !int.TryParse(colStr, out var c))
            return false;

        filePath = fileCandidate;
        line = l - 1; // convert to 0-based
        col = c - 1;  // convert to 0-based
        return true;
    }

    private static string? ResolveWorkspaceRoot()
    {
        var target = Common.DotnetWorkspaceResolver.FindTarget(Directory.GetCurrentDirectory());
        if (target is null)
            return null;
        return Path.GetDirectoryName(Path.GetFullPath(target));
    }

    private static async Task<bool> SpawnDaemonAsync(string workspaceRoot, CommandContext ctx)
    {
        try
        {
            // Spawn daemon as background process via tk __lsp-daemon
            var tkPath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "tk";
            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tkPath,
                    ArgumentList = { "__lsp-daemon", workspaceRoot },
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true,
                }
            };
            proc.Start();

            // Socket-first: the daemon binds the socket before any server interaction,
            // so it should appear within ~500ms of process start.
            var socketPath = DaemonSocket.GetSocketPath(workspaceRoot);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(socketPath) && !cts.Token.IsCancellationRequested)
                await Task.Delay(100, cts.Token).ConfigureAwait(false);

            return File.Exists(socketPath);
        }
        catch (Exception ex)
        {
            ctx.Err.WriteLine($"tk refs: failed to spawn daemon: {ex.Message}");
            return false;
        }
    }
}
