using Tk.Lsp;

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

        // Parse position or symbol
        DaemonRequest request;
        string? workspaceRoot;
        if (LspCommandHelpers.TryParsePosition(arg, out var filePath, out var line, out var col))
        {
            // The daemon builds a file:// URI from this path, which requires an absolute path.
            workspaceRoot = LspCommandHelpers.ResolveWorkspaceRoot(filePath);
            request = new DaemonRequest("refs", Path.GetFullPath(filePath), line, col, null);
        }
        else
        {
            // Symbol name: let the daemon resolve it via workspace/symbol
            workspaceRoot = LspCommandHelpers.ResolveWorkspaceRoot();
            request = new DaemonRequest("refs", null, 0, 0, arg);
        }

        if (workspaceRoot is null)
        {
            ctx.Err.WriteLine("tk refs: could not find workspace root (.sln or .csproj)");
            return 1;
        }

        // Connect and send request (120s total — daemon may still be doing cold workspace load)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var response = await DaemonClient.SendAsync(workspaceRoot, request, cts.Token).ConfigureAwait(false);

            if (!response.Success)
            {
                ctx.Err.WriteLine($"tk refs: {response.Error ?? "unknown error"}");
                return 1;
            }

            if (response.Candidates is { Length: > 0 } candidates)
            {
                ctx.Out.WriteLine(RefsFormatter.FormatCandidates(arg, candidates));
                return 0;
            }

            var locations = response.Locations ?? [];
            ctx.ResultCount = locations.Length;
            ctx.Out.WriteLine(RefsFormatter.Format(arg, locations));
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
}
