namespace Tk.Filters;

/// <summary>
/// Pure helpers for the `tk grep` thin passthrough over system grep:
/// auto-adds -r when the target is a directory (system grep isn't recursive by
/// default, unlike ripgrep), and detects a request for tk's own compact help
/// instead of letting --help pass through to grep's own usage text.
/// </summary>
public static class GrepArgsHelper
{
    public static bool HasRecursiveFlag(string[] args) =>
        args.Any(a => a is "-r" or "-R" or "--recursive");

    /// <summary>
    /// True when <paramref name="args"/> is a `grep` invocation targeting a directory
    /// without an explicit recursive flag already present.
    /// </summary>
    public static bool NeedsRecursiveFlag(string[] args, Func<string, bool> isDirectory)
    {
        if (args.Length == 0 || !args[0].Equals("grep", StringComparison.OrdinalIgnoreCase))
            return false;

        if (HasRecursiveFlag(args))
            return false;

        return args.Skip(1).Any(a => !a.StartsWith('-') && isDirectory(a));
    }

    /// <summary>Returns args with -r inserted right after the command when needed, otherwise unchanged.</summary>
    public static string[] EnsureRecursive(string[] args, Func<string, bool> isDirectory)
    {
        if (!NeedsRecursiveFlag(args, isDirectory))
            return args;

        var result = new string[args.Length + 1];
        result[0] = args[0];
        result[1] = "-r";
        Array.Copy(args, 1, result, 2, args.Length - 1);
        return result;
    }

    /// <summary>
    /// True when the user is asking for grep help rather than running a search:
    /// --help always, or -h with no pattern argument present.
    /// </summary>
    public static bool WantsOwnHelp(string[] args)
    {
        if (args.Length == 0 || !args[0].Equals("grep", StringComparison.OrdinalIgnoreCase))
            return false;

        if (args.Contains("--help"))
            return true;

        return args.Contains("-h") && !args.Skip(1).Any(a => !a.StartsWith('-'));
    }

    public static string HelpText() => """
        tk grep <pattern> <path> [flags]

        Thin wrapper over system grep with tk-compact summary output.
          - Recursive by default: if <path> is a directory and neither -r/-R/--recursive
            is given, tk adds -r automatically.
          - Any standard grep flag is passed through unmodified (-i, -n, -v, -c, -l, -w,
            -E, --include, etc).

        Output:
          grep m=<matches> f=<files> [bin=<binary matches>]
          top=<file(count)>,...                 top files by match count
          samples:                              representative match lines (pattern-centered)
            <file>:<line> <snippet>
          +N more files                         disclosed when the top= list is truncated
          hid=<hidden>/<total> (--more, --raw)  shown vs total when output was trimmed

        Use --more for wider top/sample lists, --raw for untouched grep output.

        """;
}
