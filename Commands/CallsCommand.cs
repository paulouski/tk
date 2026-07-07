using Tk.Lsp;

namespace Tk.Commands;

public sealed class CallsCommand : ICommand
{
    public string Name => "calls";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        if (ctx.Args.Length == 0)
        {
            ctx.Out.WriteLine("usage: tk calls <symbol>");
            ctx.Out.WriteLine("       tk calls <file:line:col>");
            return 1;
        }

        var arg = ctx.Args[0];

        var workspaceRoot = LspCommandHelpers.ResolveWorkspaceRoot();
        if (workspaceRoot is null)
        {
            ctx.Err.WriteLine("tk calls: could not find workspace root (.sln or .csproj)");
            return 1;
        }

        DaemonRequest request;
        if (LspCommandHelpers.TryParsePosition(arg, out var filePath, out var line, out var col))
        {
            request = new DaemonRequest("calls", Path.GetFullPath(filePath), line, col, null);
        }
        else
        {
            request = new DaemonRequest("calls", null, 0, 0, arg);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var response = await DaemonClient.SendAsync(workspaceRoot, request, cts.Token).ConfigureAwait(false);

            if (!response.Success)
            {
                ctx.Err.WriteLine($"tk calls: {response.Error ?? "unknown error"}");
                return 1;
            }

            if (response.Candidates is { Length: > 0 } candidates)
            {
                ctx.Out.WriteLine(RefsFormatter.FormatCandidates(arg, candidates, "calls"));
                return 0;
            }

            var callees = response.Callees ?? [];
            ctx.ResultCount = callees.Length;
            ctx.Out.WriteLine(CallersFormatter.Format(arg, callees, "calls"));
            // KNOWN RISK: some Roslyn language-server builds do not implement
            // callHierarchy/outgoingCalls and silently answer with an empty array even for a
            // method that provably calls others — an empty result here is ambiguous between
            // "genuinely no outgoing calls" and "server doesn't support this direction", so
            // this note is printed every time rather than risk a false "n=0".
            if (callees.Length == 0)
                ctx.Out.WriteLine("note: server returned no outgoing calls (Roslyn LS may not support callHierarchy/outgoingCalls)");
            return 0;
        }
        catch (OperationCanceledException)
        {
            ctx.Err.WriteLine("tk calls: timed out waiting for daemon response");
            return 1;
        }
        catch (Exception ex)
        {
            ctx.Err.WriteLine($"tk calls: {ex.Message}");
            return 1;
        }
    }
}
