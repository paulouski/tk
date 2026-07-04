using Tk.Lsp;

namespace Tk.Commands;

public sealed class RenameCommand : ICommand
{
    public string Name => "rename";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        var force = ctx.Args.Contains("--force");
        var positional = ctx.Args.Where(a => a != "--force").ToArray();

        if (positional.Length < 2)
        {
            ctx.Out.WriteLine("usage: tk rename <file:line:col> <newName> [--force]");
            ctx.Out.WriteLine("       <file:line:col>  position of the symbol to rename");
            ctx.Out.WriteLine("       --force          skip the name-collision safety check");
            return 1;
        }

        var posArg = positional[0];
        var newName = positional[1];

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

        var fullPath = Path.GetFullPath(filePath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            if (!force)
            {
                var conflict = await CheckForConflictAsync(ctx, workspaceRoot, fullPath, line, col, newName, cts.Token)
                    .ConfigureAwait(false);
                if (conflict is not null)
                {
                    var loc = conflict.Location;
                    var conflictPath = RenameFormatter.UriToPath(loc.Uri);
                    var label = string.IsNullOrEmpty(conflict.ContainerName)
                        ? conflict.Name
                        : $"{conflict.ContainerName}.{conflict.Name}";
                    ctx.Err.WriteLine(
                        $"tk rename: refusing — '{newName}' already exists as {conflict.Kind} {label} " +
                        $"at {conflictPath}:{loc.StartLine + 1}:{loc.StartChar + 1}; this rename would break the " +
                        "build (duplicate definition). Use --force to override.");
                    return 1;
                }
            }

            var request = new DaemonRequest("rename", fullPath, line, col, null, newName);
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

    /// <summary>
    /// Heuristically checks whether renaming the symbol at (filePath,line,col) to newName
    /// would collide with an existing symbol of that name in the same container (namespace or
    /// type). Returns the conflicting match to refuse the rename, or null if it looks safe —
    /// including when the check cannot be performed reliably (e.g. a local variable/parameter,
    /// which isn't indexed by workspace/symbol at all): in that case a short note is printed
    /// and the rename proceeds rather than silently skipping the check.
    /// </summary>
    private static async Task<SymbolMatch?> CheckForConflictAsync(
        CommandContext ctx, string workspaceRoot, string filePath, int line, int col, string newName,
        CancellationToken ct)
    {
        var defResp = await DaemonClient.SendAsync(
            workspaceRoot, new DaemonRequest("def", filePath, line, col, null), ct).ConfigureAwait(false);
        if (!defResp.Success || (defResp.Locations?.Length ?? 0) != 1)
            return null; // Can't reliably resolve a single declaration; skip the check.

        var defLoc = defResp.Locations![0];

        string oldName;
        try
        {
            var defPath = RenameFormatter.UriToPath(defLoc.Uri);
            var lines = await File.ReadAllLinesAsync(defPath, ct).ConfigureAwait(false);
            if (defLoc.StartLine < 0 || defLoc.StartLine >= lines.Length)
                return null;
            oldName = RenameConflictChecker.ExtractIdentifier(lines[defLoc.StartLine], defLoc.StartChar);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrEmpty(oldName) || oldName == newName)
            return null;

        var oldSymbolsResp = await DaemonClient.SendAsync(
            workspaceRoot, new DaemonRequest("symbols", null, 0, 0, oldName), ct).ConfigureAwait(false);
        if (!oldSymbolsResp.Success)
            return null;

        var oldMatches = oldSymbolsResp.Candidates ?? [];
        if (oldMatches.Length == 0)
            return null; // Not indexed at symbol level (e.g. a local variable/parameter) — nothing to compare against.

        var oldSelf = RenameConflictChecker.FindDeclarationMatch(oldMatches, defLoc.Uri, defLoc.StartLine);
        if (oldSelf is null)
        {
            ctx.Out.WriteLine(
                $"tk rename: note: could not reliably verify no-collision for '{oldName}' (ambiguous); proceeding.");
            return null;
        }

        var newSymbolsResp = await DaemonClient.SendAsync(
            workspaceRoot, new DaemonRequest("symbols", null, 0, 0, newName), ct).ConfigureAwait(false);
        if (!newSymbolsResp.Success)
            return null;

        var newMatches = newSymbolsResp.Candidates ?? [];
        return RenameConflictChecker.FindConflict(oldSelf.ContainerName, newMatches);
    }
}
