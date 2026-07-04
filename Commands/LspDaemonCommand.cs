using System.Runtime.InteropServices;
using Tk.Lsp;

namespace Tk.Commands;

/// <summary>
/// Hidden internal command that launches the LSP daemon process for a workspace root.
/// Usage: tk __lsp-daemon &lt;workspaceRoot&gt;
/// </summary>
public sealed class LspDaemonCommand : ICommand
{
    public string Name => "__lsp-daemon";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        if (ctx.Args.Length == 0)
        {
            ctx.Err.WriteLine("__lsp-daemon: workspace root argument required");
            return 1;
        }

        var workspaceRoot = ctx.Args[0];
        if (!Directory.Exists(workspaceRoot))
        {
            ctx.Err.WriteLine($"__lsp-daemon: workspace root does not exist: {workspaceRoot}");
            return 1;
        }

        var backend = new CSharpBackend();
        var daemon = new LspDaemon(workspaceRoot, backend);

        try
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            // A plain `kill <pid>` (SIGTERM) would otherwise terminate this process without
            // running LspDaemon.RunAsync's cleanup (which kills the Roslyn child and removes
            // the socket/pid files). Route it through the same graceful cancellation path.
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx2 =>
            {
                ctx2.Cancel = true;
                cts.Cancel();
            });

            await daemon.RunAsync(cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            ctx.Err.WriteLine($"__lsp-daemon: {ex.Message}");
            return 1;
        }
    }
}
