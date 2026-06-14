namespace Tk.Filters;

public static class FilterRegistry
{
    public static IOutputFilter Resolve(string[] args, DetailLevel detailLevel)
    {
        if (args.Length == 0)
            return new PassthroughFilter();

        var command = args[0].ToLowerInvariant();

        if (command == "dotnet")
        {
            var subcommand = FindDotnetSubcommand(args);
            return subcommand?.ToLowerInvariant() switch
            {
                "build" => new DotnetBuildFilter(),
                "test" => new DotnetTestFilter(),
                "restore" => new DotnetRestoreFilter(),
                _ => new PassthroughFilter()
            };
        }

        if (command is "grep" or "rg")
            return new GrepFilter(command, detailLevel, FindSearchPattern(args, command));

        if (command == "find")
            return new FindFilter(detailLevel);

        if (command == "git")
        {
            var subcommand = FindGitSubcommand(args);
            return subcommand?.ToLowerInvariant() switch
            {
                "status" => new GitStatusFilter(detailLevel),
                "log" => new GitLogFilter(),
                "diff" or "show" => new GitDiffFilter(detailLevel),
                "add" or "commit" or "push" or "pull" or "fetch"
                    or "stash" or "branch" or "checkout" or "switch"
                    or "merge" or "rebase" or "reset" or "tag" => new GitCompactFilter(),
                _ => new PassthroughFilter()
            };
        }

        return new PassthroughFilter();
    }

    private static string? FindDotnetSubcommand(string[] args)
    {
        foreach (var arg in args.Skip(1))
        {
            if (!arg.StartsWith('-'))
                return arg;
        }

        return null;
    }

    private static string? FindGitSubcommand(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith('-'))
                return arg;

            if (GitOptionNeedsValue(arg) && !arg.Contains('='))
                i++;
        }

        return null;
    }

    private static bool GitOptionNeedsValue(string arg) => arg is
        "-c" or "-C" or "--git-dir" or "--work-tree" or "--namespace"
        or "--super-prefix" or "--config-env";

    private static string? FindSearchPattern(string[] args, string command)
    {
        var endOfOptions = false;
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (!endOfOptions && arg == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (!endOfOptions && arg.StartsWith('-'))
            {
                if (SearchOptionIsPattern(arg) && !arg.Contains('=') && i + 1 < args.Length)
                    return args[i + 1];
                if (SearchOptionIsPattern(arg) && arg.Contains('='))
                    return arg[(arg.IndexOf('=') + 1)..];
                if (SearchOptionNeedsValue(arg, command) && !arg.Contains('='))
                    i++;
                continue;
            }

            return arg;
        }

        return null;
    }

    private static bool SearchOptionNeedsValue(string arg, string command) =>
        command == "rg"
            ? arg is "-g" or "--glob" or "-t" or "--type" or "-T" or "--type-not"
                or "-f" or "--file" or "-m" or "--max-count"
                or "-A" or "--after-context" or "-B" or "--before-context" or "-C" or "--context"
            : arg is "-f" or "-m" or "-A" or "-B" or "-C";

    private static bool SearchOptionIsPattern(string arg) =>
        arg is "-e" or "--regexp" || arg.StartsWith("--regexp=", StringComparison.Ordinal);
}
