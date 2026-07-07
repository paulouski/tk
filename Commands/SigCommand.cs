using Tk.Lsp;

namespace Tk.Commands;

public sealed class SigCommand : ICommand
{
    public string Name => "sig";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        if (ctx.Args.Length == 0)
        {
            ctx.Out.WriteLine("usage: tk sig <symbol>");
            ctx.Out.WriteLine("       tk sig <file:line:col>");
            return 1;
        }

        var arg = ctx.Args[0];

        var workspaceRoot = LspCommandHelpers.ResolveWorkspaceRoot();
        if (workspaceRoot is null)
        {
            ctx.Err.WriteLine("tk sig: could not find workspace root (.sln or .csproj)");
            return 1;
        }

        DaemonRequest request;
        if (LspCommandHelpers.TryParsePosition(arg, out var filePath, out var line, out var col))
        {
            request = new DaemonRequest("sig", Path.GetFullPath(filePath), line, col, null);
        }
        else
        {
            request = new DaemonRequest("sig", null, 0, 0, arg);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var response = await DaemonClient.SendAsync(workspaceRoot, request, cts.Token).ConfigureAwait(false);

            if (!response.Success)
            {
                ctx.Err.WriteLine($"tk sig: {response.Error ?? "unknown error"}");
                return 1;
            }

            if (response.Candidates is { Length: > 0 } candidates)
            {
                ctx.Out.WriteLine(RefsFormatter.FormatCandidates(arg, candidates, "sig"));
                return 0;
            }

            ctx.ResultCount = response.Hover is null ? 0 : 1;
            ctx.Out.WriteLine(SigFormatter.Format(arg, response.Hover));
            return 0;
        }
        catch (OperationCanceledException)
        {
            ctx.Err.WriteLine("tk sig: timed out waiting for daemon response");
            return 1;
        }
        catch (Exception ex)
        {
            ctx.Err.WriteLine($"tk sig: {ex.Message}");
            return 1;
        }
    }
}
