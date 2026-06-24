namespace Tk.Commands;

/// <summary>
/// Moves a source file preserving git history (via <c>git mv</c> when the file is tracked,
/// filesystem move otherwise). Lets agents relocate or rename a file without the
/// delete-and-recreate pattern that loses git history and re-emits the whole file.
/// </summary>
public sealed class MvCommand : ICommand
{
    public string Name => "mv";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        if (ctx.Args.Length < 2)
        {
            ctx.Out.WriteLine("usage: tk mv <old> <new>");
            ctx.Out.WriteLine("       Moves a file preserving git history (git mv when tracked).");
            ctx.Out.WriteLine("       If <new> is an existing directory, moves into it.");
            ctx.Out.WriteLine("       Directory moves not supported yet.");
            return 1;
        }

        var oldArg = ctx.Args[0];
        var newArg = ctx.Args[1];

        // Resolve to absolute paths so all subsequent operations are unambiguous.
        var oldPath = Path.GetFullPath(oldArg);
        var newPath = Path.GetFullPath(newArg);

        // Validate source
        if (!File.Exists(oldPath))
        {
            if (Directory.Exists(oldPath))
            {
                ctx.Err.WriteLine($"tk mv: directory moves not supported yet: {oldArg}");
                return 1;
            }
            ctx.Err.WriteLine($"tk mv: source not found: {oldArg}");
            return 1;
        }

        // Unix mv semantics: if destination is an existing directory, move into it.
        if (Directory.Exists(newPath))
            newPath = Path.Combine(newPath, Path.GetFileName(oldPath));

        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Err.WriteLine("tk mv: source and destination are the same");
            return 1;
        }

        var (gitUsed, moveError) = await MoveFileAsync(ctx, oldPath, newPath).ConfigureAwait(false);
        if (moveError is not null)
        {
            ctx.Err.WriteLine($"tk mv: {moveError}");
            return 1;
        }

        ctx.Out.WriteLine(FormatOutput(oldArg, newArg, gitUsed));

        // A move into a different directory usually changes the file's namespace
        // (folder-based convention). tk does not rewrite it — remind the agent to.
        var note = CrossDirectoryNote(oldPath, newPath);
        if (note is not null)
            ctx.Out.WriteLine(note);

        return 0;
    }

    /// <summary>
    /// Moves the file, preferring <c>git mv</c> when the file is git-tracked.
    /// Returns (gitUsed, errorMessage). errorMessage is null on success.
    /// </summary>
    private static async Task<(bool GitUsed, string? Error)> MoveFileAsync(
        CommandContext ctx, string oldPath, string newPath)
    {
        var destDir = Path.GetDirectoryName(newPath)!;

        // Detect git context
        var isGit = await IsInsideGitWorkTreeAsync(ctx).ConfigureAwait(false);
        if (isGit)
        {
            var isTracked = await IsGitTrackedAsync(ctx, oldPath).ConfigureAwait(false);
            if (isTracked)
            {
                // git mv requires the destination directory to exist first.
                Directory.CreateDirectory(destDir);
                var (exitCode, _, stderr) = await ctx.Process
                    .RunAsync(["git", "mv", oldPath, newPath])
                    .ConfigureAwait(false);
                if (exitCode != 0)
                    return (false, $"git mv failed: {stderr.Trim()}");
                return (true, null);
            }
        }

        // Filesystem fallback
        try
        {
            Directory.CreateDirectory(destDir);
            File.Move(oldPath, newPath);
            return (false, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<bool> IsInsideGitWorkTreeAsync(CommandContext ctx)
    {
        try
        {
            var (exit, stdout, _) = await ctx.Process
                .RunAsync(["git", "rev-parse", "--is-inside-work-tree"])
                .ConfigureAwait(false);
            return exit == 0 && stdout.Trim() == "true";
        }
        catch { return false; }
    }

    private static async Task<bool> IsGitTrackedAsync(CommandContext ctx, string filePath)
    {
        try
        {
            var (exit, _, _) = await ctx.Process
                .RunAsync(["git", "ls-files", "--error-unmatch", filePath])
                .ConfigureAwait(false);
            return exit == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Formats the compact output line.
    /// Example: <c>mv Old.cs -> New.cs git=yes</c>
    /// </summary>
    internal static string FormatOutput(string oldArg, string newArg, bool gitUsed)
    {
        var git = gitUsed ? "yes" : "no";
        return $"mv {oldArg} -> {newArg} git={git}";
    }

    /// <summary>
    /// Returns a reminder to check the namespace/references when the file moved to a
    /// different directory, or null for a same-directory rename (namespace unchanged).
    /// </summary>
    internal static string? CrossDirectoryNote(string oldPath, string newPath) =>
        string.Equals(Path.GetDirectoryName(oldPath), Path.GetDirectoryName(newPath), StringComparison.Ordinal)
            ? null
            : "note: moved across directories — check the file's namespace and update references";
}
