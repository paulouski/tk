using Tk.Lsp;

namespace Tk.Commands;

/// <summary>
/// Moves a source file via a plain filesystem move — never <c>git mv</c> or any other
/// index-writing git command, so this is safe to run in sandboxes that forbid touching the
/// git index. Git detects the rename itself via content similarity on the next
/// <c>git status</c>/<c>git diff</c>. Lets agents relocate or rename a file without the
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
            ctx.Out.WriteLine("       Moves a file via a plain filesystem move (never touches the git index);");
            ctx.Out.WriteLine("       git detects the rename itself on the next status/diff.");
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

        var moveError = MoveFile(oldPath, newPath);
        if (moveError is not null)
        {
            ctx.Err.WriteLine($"tk mv: {moveError}");
            return 1;
        }

        ctx.Out.WriteLine(FormatOutput(oldArg, newArg));

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
    /// Moves the file with a plain filesystem move — tracked and untracked files alike, never
    /// touching the git index. Returns the error message, or null on success.
    ///
    /// Case-only renames (e.g. Foo.cs -&gt; foo.cs) on a case-insensitive-but-case-preserving
    /// filesystem (the macOS/Windows default) work fine here: <see cref="File.Move"/> goes
    /// straight to the OS rename primitive (rename(2) / MoveFileEx), which renames the directory
    /// entry in place rather than treating same-vs-new name as "already exists" — verified
    /// directly against this filesystem, not assumed.
    /// </summary>
    private static string? MoveFile(string oldPath, string newPath)
    {
        var destDir = Path.GetDirectoryName(newPath)!;
        Directory.CreateDirectory(destDir);

        try
        {
            File.Move(oldPath, newPath);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Whether two resolved paths refer to the same source and destination. Case-sensitive:
    /// a case-only rename (e.g. Foo.cs -&gt; foo.cs) is a legitimate move — <see cref="MoveFile"/>
    /// handles it safely even on case-insensitive filesystems — so it must not be refused.
    /// </summary>
    internal static bool IsSamePath(string oldPath, string newPath) =>
        string.Equals(oldPath, newPath, StringComparison.Ordinal);

    /// <summary>
    /// Formats the compact output line.
    /// Example: <c>mv Old.cs -> New.cs</c>
    /// </summary>
    internal static string FormatOutput(string oldArg, string newArg) => $"mv {oldArg} -> {newArg}";

    /// <summary>
    /// Returns a reminder to check the namespace/references when the file moved to a
    /// different directory, or null for a same-directory rename (namespace unchanged).
    /// </summary>
    internal static string? CrossDirectoryNote(string oldPath, string newPath) =>
        string.Equals(Path.GetDirectoryName(oldPath), Path.GetDirectoryName(newPath), StringComparison.Ordinal)
            ? null
            : LegacyNamespaceNote;
}
