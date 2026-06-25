using Tk.Lsp;

namespace Tk.Commands;

public sealed class CallersCommand : ICommand
{
    public string Name => "callers";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        if (ctx.Args.Length == 0)
        {
            ctx.Out.WriteLine("usage: tk callers <symbol>");
            ctx.Out.WriteLine("       tk callers <file:line:col>");
            return 1;
        }

        var arg = ctx.Args[0];

        var workspaceRoot = LspCommandHelpers.ResolveWorkspaceRoot();
        if (workspaceRoot is null)
        {
            ctx.Err.WriteLine("tk callers: could not find workspace root (.sln or .csproj)");
            return 1;
        }

        DaemonRequest request;
        if (LspCommandHelpers.TryParsePosition(arg, out var filePath, out var line, out var col))
        {
            request = new DaemonRequest("callers", Path.GetFullPath(filePath), line, col, null);
        }
        else
        {
            request = new DaemonRequest("callers", null, 0, 0, arg);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var response = await DaemonClient.SendAsync(workspaceRoot, request, cts.Token).ConfigureAwait(false);

            if (!response.Success)
            {
                ctx.Err.WriteLine($"tk callers: {response.Error ?? "unknown error"}");
                return 1;
            }

            if (response.Candidates is { Length: > 0 } candidates)
            {
                ctx.Out.WriteLine(RefsFormatter.FormatCandidates(arg, candidates, "callers"));
                return 0;
            }

            var callers = response.Callers ?? [];
            ctx.ResultCount = callers.Length;
            ctx.Out.WriteLine(CallersFormatter.Format(arg, callers));
            return 0;
        }
        catch (OperationCanceledException)
        {
            ctx.Err.WriteLine("tk callers: timed out waiting for daemon response");
            return 1;
        }
        catch (Exception ex)
        {
            ctx.Err.WriteLine($"tk callers: {ex.Message}");
            return 1;
        }
    }
}
