using Tk.Lsp;

namespace Tk.Commands;

public sealed class SymCommand : ICommand
{
    public string Name => "sym";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        if (ctx.Args.Length == 0)
        {
            ctx.Out.WriteLine("usage: tk sym <query>");
            return 1;
        }

        var query = ctx.Args[0];

        var workspaceRoot = LspCommandHelpers.ResolveWorkspaceRoot();
        if (workspaceRoot is null)
        {
            ctx.Err.WriteLine("tk sym: could not find workspace root (.sln or .csproj)");
            return 1;
        }

        var request = new DaemonRequest("sym", null, 0, 0, query);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var response = await DaemonClient.SendAsync(workspaceRoot, request, cts.Token).ConfigureAwait(false);

            if (!response.Success)
            {
                ctx.Err.WriteLine($"tk sym: {response.Error ?? "unknown error"}");
                return 1;
            }

            var matches = response.Candidates ?? [];
            ctx.ResultCount = matches.Length;
            var cap = ctx.DetailLevel == DetailLevel.More ? SymFormatter.MoreCap : SymFormatter.DefaultCap;
            ctx.Out.WriteLine(SymFormatter.Format(query, matches, cap));
            return 0;
        }
        catch (OperationCanceledException)
        {
            ctx.Err.WriteLine("tk sym: timed out waiting for daemon response");
            return 1;
        }
        catch (Exception ex)
        {
            ctx.Err.WriteLine($"tk sym: {ex.Message}");
            return 1;
        }
    }
}
