using Tk.Lsp;

namespace Tk.Commands;

public sealed class DiagCommand : ICommand
{
    public string Name => "diag";

    // Cap on the number of .cs files pulled per invocation when the scope resolves to a
    // project or directory (a single file is always queried in full regardless of the cap).
    // Keeps one `tk diag` call bounded against a large solution; `--more` raises it, and
    // narrowing the path scopes it further. Diagnostics are pulled one file at a time from
    // the warm daemon (see docs/lsp-daemon-architecture.md), so this is also a rough bound on
    // wall-clock time for a directory/project-scoped call.
    internal const int DefaultMaxFiles = 300;
    internal const int MoreMaxFiles = 2000;

    public async Task<int> RunAsync(CommandContext ctx)
    {
        var errorsOnly = ctx.Args.Contains("--errors");
        var changed = ctx.Args.Contains("--changed");
        var positional = ctx.Args.Where(a => a != "--errors" && a != "--changed").ToArray();

        List<string> files;
        int totalCount;

        if (changed)
        {
            var (exitCode, stdout, _) = await ctx.Process.RunAsync(["git", "status", "--porcelain=v1"]).ConfigureAwait(false);
            if (exitCode != 0)
            {
                ctx.Err.WriteLine("tk diag --changed: not a git repository (or git status failed)");
                return 1;
            }

            files = ParseChangedCsFiles(stdout);
            totalCount = files.Count;

            if (files.Count == 0)
            {
                ctx.Out.WriteLine("ok diag --changed: no changed .cs files");
                return 0;
            }
        }
        else
        {
            if (positional.Length == 0)
            {
                ctx.Out.WriteLine("usage: tk diag <file|project|dir>");
                ctx.Out.WriteLine("       tk diag <path> --errors   Errors only");
                ctx.Out.WriteLine("       tk diag --changed         Diagnostics for changed .cs files (staged+modified+untracked)");
                return 1;
            }

            var pathArg = positional[0];
            var fullPath = Path.GetFullPath(pathArg);

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                ctx.Err.WriteLine($"tk diag: {pathArg}: no such file or directory");
                return 1;
            }

            var maxFiles = ctx.DetailLevel == DetailLevel.More ? MoreMaxFiles : DefaultMaxFiles;
            (files, totalCount) = ResolveScope(fullPath, maxFiles);
            if (files.Count == 0)
            {
                ctx.Err.WriteLine($"tk diag: {pathArg}: no .cs files found in scope");
                return 1;
            }
        }

        var workspaceRoot = LspCommandHelpers.ResolveWorkspaceRoot();
        if (workspaceRoot is null)
        {
            ctx.Err.WriteLine("tk diag: could not find workspace root (.sln or .csproj)");
            return 1;
        }

        var request = new DaemonRequest("diag", null, 0, 0, null, Paths: [.. files]);

        // Connect and send request (120s total — daemon may still be doing cold workspace
        // load, or this may be a large batch of files).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var response = await DaemonClient.SendAsync(workspaceRoot, request, cts.Token).ConfigureAwait(false);

            if (!response.Success)
            {
                ctx.Err.WriteLine($"tk diag: {response.Error ?? "unknown error"}");
                return 1;
            }

            var byFile = response.DiagnosticsByFile ?? [];
            var (output, errorCount) = DiagFormatter.Format(byFile, errorsOnly, files.Count, totalCount);
            ctx.ResultCount = byFile.Sum(f => f.Diagnostics.Length);
            ctx.Out.WriteLine(output);
            return errorCount > 0 ? 2 : 0;
        }
        catch (OperationCanceledException)
        {
            ctx.Err.WriteLine("tk diag: timed out waiting for daemon response");
            return 1;
        }
        catch (Exception ex)
        {
            ctx.Err.WriteLine($"tk diag: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Resolves a diag scope path to the list of .cs files to query: the file itself when
    /// <paramref name="fullPath"/> is a .cs file; otherwise every .cs file recursively under
    /// its directory (a .csproj's own directory, or the directory itself), excluding bin/obj.
    /// Files are sorted for determinism and capped at <paramref name="maxFiles"/> (first N
    /// kept) — the caller discloses the cap via the second tuple element, the true total.
    /// </summary>
    internal static (List<string> Files, int TotalCount) ResolveScope(string fullPath, int maxFiles)
    {
        if (File.Exists(fullPath) && fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return ([fullPath], 1);

        var dir = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath)! : fullPath;
        var all = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsUnderExcludedDir(dir, f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var files = all.Count > maxFiles ? all.Take(maxFiles).ToList() : all;
        return (files, all.Count);
    }

    private static bool IsUnderExcludedDir(string root, string filePath)
    {
        var relative = Path.GetRelativePath(root, filePath);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p is "bin" or "obj");
    }

    /// <summary>
    /// Parses `git status --porcelain=v1` output into a de-duplicated, absolute-path list of
    /// changed .cs files (staged + modified + untracked — the same set `tk changes` surfaces).
    /// Each porcelain line is "XY path" (or "XY old -> new" for a rename/copy, in which case
    /// the new path is kept); paths git quotes for special characters have that quoting stripped.
    /// </summary>
    internal static List<string> ParseChangedCsFiles(string porcelain)
    {
        var files = new List<string>();
        foreach (var rawLine in porcelain.Split('\n'))
        {
            if (rawLine.Length < 4) continue;
            var path = rawLine[3..].Trim();

            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) path = path[(arrow + 4)..];
            path = path.Trim('"');

            if (path.Length == 0 || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;

            files.Add(Path.GetFullPath(path));
        }

        // Skip deletions (git still reports them as a changed path, but there is no file left
        // to pull diagnostics for).
        return [.. files
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];
    }
}
