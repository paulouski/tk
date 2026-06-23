using Tk.Lsp;

namespace Tk.Commands;

public sealed class RenameCommand : ICommand
{
    public string Name => "rename";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        if (ctx.Args.Length < 2)
        {
            ctx.Out.WriteLine("usage: tk rename <file:line:col> <newName>");
            ctx.Out.WriteLine("       <file:line:col>  position of the symbol to rename");
            return 1;
        }

        var posArg = ctx.Args[0];
        var newName = ctx.Args[1];

        if (!LspCommandHelpers.TryParsePosition(posArg, out var filePath, out var line, out var col))
        {
            ctx.Err.WriteLine("tk rename: expected <file:line:col>");
            return 1;
        }

        if (string.IsNullOrEmpty(newName))
        {
            ctx.Err.WriteLine("tk rename: newName must not be empty");
            return 1;
        }

        var workspaceRoot = LspCommandHelpers.ResolveWorkspaceRoot();
        if (workspaceRoot is null)
        {
            ctx.Err.WriteLine("tk rename: could not find workspace root (.sln or .csproj)");
            return 1;
        }

        var request = new DaemonRequest("rename", Path.GetFullPath(filePath), line, col, null, newName);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var resp = await DaemonClient.SendAsync(workspaceRoot, request, cts.Token).ConfigureAwait(false);

            if (!resp.Success)
            {
                ctx.Err.WriteLine($"tk rename: {resp.Error}");
                return 1;
            }

            var files = resp.Edits ?? [];

            if (files.Length == 0)
            {
                ctx.Out.WriteLine($"rename {posArg} -> {newName} n=0 f=0");
                return 0;
            }

            foreach (var fileEdits in files)
            {
                var path = RenameFormatter.UriToPath(fileEdits.Uri);
                var text = await File.ReadAllTextAsync(path, cts.Token).ConfigureAwait(false);
                var updated = RenameEditApplier.Apply(text, fileEdits.Edits);
                await File.WriteAllTextAsync(path, updated, cts.Token).ConfigureAwait(false);
            }

            ctx.Out.WriteLine(RenameFormatter.Format(posArg, newName, files));
            return 0;
        }
        catch (OperationCanceledException)
        {
            ctx.Err.WriteLine("tk rename: timed out waiting for daemon response");
            return 1;
        }
        catch (Exception ex)
        {
            ctx.Err.WriteLine($"tk rename: {ex.Message}");
            return 1;
        }
    }
}
