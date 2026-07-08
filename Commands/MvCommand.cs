using Tk.Lsp;

namespace Tk.Commands;

/// <summary>
/// Moves a source file preserving git history (via <c>git mv</c> when the file is tracked,
/// filesystem move otherwise). Lets agents relocate or rename a file without the
/// delete-and-recreate pattern that loses git history and re-emits the whole file.
///
/// For a .cs file moved across directories, this also fixes the namespace by default (mirroring
/// the IDE convention: Rider/VS adjust a file's namespace automatically when you drag it to a
/// new folder) — see <see cref="TryFixNamespaceAsync"/>. This only ever engages when every
/// precondition can be verified cheaply and safely; anything uncertain degrades to the plain
/// move-only behavior rather than guessing. <c>--no-fix-ns</c> opts out entirely; <c>--ns</c>
/// pins the target namespace explicitly.
/// </summary>
public sealed class MvCommand : ICommand
{
    public string Name => "mv";

    private const string LegacyNamespaceNote =
        "note: moved across directories — check the file's namespace and update references";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        var noFixNs = ctx.Args.Contains("--no-fix-ns");
        var nsOverride = ExtractNsOverride(ctx.Args, out var nsFlagError);
        var positional = StripFlags(ctx.Args);

        if (nsFlagError is not null)
        {
            ctx.Err.WriteLine($"tk mv: {nsFlagError}");
            return 1;
        }

        if (positional.Length < 2)
        {
            ctx.Out.WriteLine("usage: tk mv <old> <new> [--no-fix-ns] [--ns <namespace>]");
            ctx.Out.WriteLine("       Moves a file preserving git history (git mv when tracked).");
            ctx.Out.WriteLine("       If <new> is an existing directory, moves into it.");
            ctx.Out.WriteLine("       Directory moves not supported yet.");
            ctx.Out.WriteLine("       For a .cs file moved across directories, the namespace is fixed to match");
            ctx.Out.WriteLine("       the new path by default (needs the lsp module); referencing files are");
            ctx.Out.WriteLine("       patched with a `using` directive as needed.");
            ctx.Out.WriteLine("       --no-fix-ns       Skip the namespace fix; move only (old behavior).");
            ctx.Out.WriteLine("       --ns <namespace>  Use this namespace instead of the computed convention.");
            return 1;
        }

        var oldArg = positional[0];
        var newArg = positional[1];

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

        if (IsSamePath(oldPath, newPath))
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

        var isCsFile = newPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
        var movedAcrossDirectories = CrossDirectoryNote(oldPath, newPath) is not null;

        // Non-.cs files and same-directory renames have no namespace implication at all — the
        // move is already complete and there's nothing to say about namespaces.
        if (!isCsFile || !movedAcrossDirectories)
            return 0;

        if (noFixNs)
        {
            ctx.Out.WriteLine(LegacyNamespaceNote);
            return 0;
        }

        var result = await NamespaceFixer.TryFixNamespaceAsync(oldPath, newPath, nsOverride).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case NsFixOutcome.Fixed:
                ctx.Out.WriteLine($"ok mv ns={result.OldNamespace}->{result.NewNamespace} refs-updated={result.RefsUpdated}");
                return 0;
            case NsFixOutcome.Unchanged:
                ctx.Out.WriteLine($"ok mv ns={result.OldNamespace} unchanged refs-updated=0");
                return 0;
            case NsFixOutcome.Degraded:
                ctx.Out.WriteLine(LegacyNamespaceNote);
                ctx.Out.WriteLine($"ns not fixed: {result.Message}");
                return 0;
            default: // Failed — the declaration was already rewritten; this needs the user's attention.
                ctx.Err.WriteLine(result.Message);
                return 1;
        }
    }

    /// <summary>
    /// Extracts the value of a `--ns &lt;namespace&gt;` flag, if present. Sets
    /// <paramref name="error"/> (and returns null) when `--ns` appears without a following value.
    /// </summary>
    internal static string? ExtractNsOverride(string[] args, out string? error)
    {
        error = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != "--ns") continue;
            if (i + 1 >= args.Length)
            {
                error = "--ns requires a value";
                return null;
            }
            return args[i + 1];
        }
        return null;
    }

    /// <summary>
    /// Removes `--no-fix-ns` and `--ns &lt;value&gt;` from the argument list, returning the
    /// remaining positional arguments (old path, new path).
    /// </summary>
    internal static string[] StripFlags(string[] args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--no-fix-ns") continue;
            if (args[i] == "--ns") { i++; continue; } // also skip its value
            result.Add(args[i]);
        }
        return [.. result];
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
    /// Whether two resolved paths refer to the same source and destination. Case-sensitive:
    /// a case-only rename (e.g. Foo.cs -&gt; foo.cs) is a legitimate move — plain `git mv`
    /// handles it correctly even on case-insensitive filesystems — so it must not be refused.
    /// </summary>
    internal static bool IsSamePath(string oldPath, string newPath) =>
        string.Equals(oldPath, newPath, StringComparison.Ordinal);

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
            : LegacyNamespaceNote;
}
