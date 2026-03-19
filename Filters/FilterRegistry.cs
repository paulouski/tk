namespace Tk.Filters;

public static class FilterRegistry
{
    public static IOutputFilter Resolve(string[] args)
    {
        if (args.Length == 0)
            return new PassthroughFilter();

        var command = args[0].ToLowerInvariant();

        if (command == "dotnet" && args.Length > 1)
        {
            return args[1].ToLowerInvariant() switch
            {
                "build" => new DotnetBuildFilter(),
                "test" => new DotnetTestFilter(),
                "restore" => new DotnetRestoreFilter(),
                _ => new PassthroughFilter()
            };
        }

        if (command == "git" && args.Length > 1)
        {
            return args[1].ToLowerInvariant() switch
            {
                "status" => new GitStatusFilter(),
                "log" => new GitLogFilter(),
                "diff" or "show" => new GitDiffFilter(),
                "add" or "commit" or "push" or "pull" or "fetch"
                    or "stash" or "branch" or "checkout" or "switch"
                    or "merge" or "rebase" or "reset" or "tag" => new GitCompactFilter(),
                _ => new PassthroughFilter()
            };
        }

        return new PassthroughFilter();
    }
}
