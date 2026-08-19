using Tk.Lsp;

namespace Tk.Commands;

public sealed class DefCommand : ICommand
{
    public string Name => "def";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        if (ctx.Args.Length == 0)
        {
            ctx.Out.WriteLine("usage: tk def <symbol>");
            ctx.Out.WriteLine("       tk def <file:line:col>");
            return 1;
        }

        var arg = ctx.Args[0];

        DaemonRequest request;
        string? workspaceRoot;
        if (LspCommandHelpers.TryParsePosition(arg, out var filePath, out var line, out var col))
        {
            workspaceRoot = LspCommandHelpers.ResolveWorkspaceRoot(filePath);
            request = new DaemonRequest("def", Path.GetFullPath(filePath), line, col, null);
        }
        else
        {
            workspaceRoot = LspCommandHelpers.ResolveWorkspaceRoot();
            request = new DaemonRequest("def", null, 0, 0, arg);
        }

        if (workspaceRoot is null)
        {
            ctx.Err.WriteLine("tk def: could not find workspace root (.sln or .csproj)");
            return 1;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var response = await DaemonClient.SendAsync(workspaceRoot, request, cts.Token).ConfigureAwait(false);

            if (!response.Success)
            {
                ctx.Err.WriteLine($"tk def: {response.Error ?? "unknown error"}");
                return 1;
            }

            if (response.Candidates is { Length: > 0 } candidates)
            {
                ctx.Out.WriteLine(RefsFormatter.FormatCandidates(arg, candidates, "def"));
                return 0;
            }

            var locations = response.Locations ?? [];
            ctx.ResultCount = locations.Length;
            ctx.Out.WriteLine(DefFormatter.Format(arg, locations));
            return 0;
        }
        catch (OperationCanceledException)
        {
            ctx.Err.WriteLine("tk def: timed out waiting for daemon response");
            return 1;
        }
        catch (Exception ex)
        {
            ctx.Err.WriteLine($"tk def: {ex.Message}");
            return 1;
        }
    }
}
