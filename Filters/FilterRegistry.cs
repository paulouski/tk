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
            return new GrepFilter(command, detailLevel);

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
}
