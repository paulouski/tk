using Tk.Lsp;

namespace Tk.Commands;

public sealed class FixCommand : ICommand
{
    public string Name => "fix";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        if (ctx.Args.Length == 0)
        {
            ctx.Out.WriteLine("usage: tk fix <file>");
            ctx.Out.WriteLine("       Adds missing usings / removes unnecessary usings only (no other edits)");
            return 1;
        }

        var pathArg = ctx.Args[0];
        var fullPath = Path.GetFullPath(pathArg);

        if (!File.Exists(fullPath))
        {
            ctx.Err.WriteLine($"tk fix: {pathArg}: no such file");
            return 1;
        }

        var workspaceRoot = LspCommandHelpers.ResolveWorkspaceRoot();
        if (workspaceRoot is null)
        {
            ctx.Err.WriteLine("tk fix: could not find workspace root (.sln or .csproj)");
            return 1;
        }

        var request = new DaemonRequest("fix", fullPath, 0, 0, null);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var response = await DaemonClient.SendAsync(workspaceRoot, request, cts.Token).ConfigureAwait(false);

            if (!response.Success)
            {
                ctx.Err.WriteLine($"tk fix: {response.Error ?? "unknown error"}");
                return 1;
            }

            var summary = response.Fix;
            if (summary is null || !summary.Supported)
            {
                ctx.Out.WriteLine(FixFormatter.FormatUnsupported(pathArg, summary));
                return 1;
            }

            var edits = response.Edits ?? [];
            if (edits.Length == 0)
            {
                ctx.Out.WriteLine(FixFormatter.FormatNothingToFix(pathArg));
                return 0;
            }

            foreach (var fileEdits in edits)
            {
                var editPath = RefsFormatter.UriToPath(fileEdits.Uri);
                var text = await File.ReadAllTextAsync(editPath, cts.Token).ConfigureAwait(false);
                var updated = RenameEditApplier.Apply(text, fileEdits.Edits);
                await File.WriteAllTextAsync(editPath, updated, cts.Token).ConfigureAwait(false);
            }

            ctx.Out.WriteLine(FixFormatter.FormatApplied(pathArg, summary));
            return 0;
        }
        catch (OperationCanceledException)
        {
            ctx.Err.WriteLine("tk fix: timed out waiting for daemon response");
            return 1;
        }
        catch (Exception ex)
        {
            ctx.Err.WriteLine($"tk fix: {ex.Message}");
            return 1;
        }
    }
}
