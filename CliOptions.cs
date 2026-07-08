namespace Tk;

public enum DetailLevel
{
    Default,
    More
}

public readonly record struct CliOptions(bool Raw, DetailLevel DetailLevel, string[] CommandArgs);

public static class CliOptionsParser
{
    public static CliOptions Parse(string[] args)
    {
        var raw = false;
        var detailLevel = DetailLevel.Default;
        var index = 0;

        // Leading global flags: --raw and --more, in any order, are recognized only while
        // they appear before the first non-flag token (the subcommand name).
        while (index < args.Length)
        {
            switch (args[index])
            {
                case "--raw":
                    raw = true;
                    index++;
                    continue;
                case "--more":
                    detailLevel = DetailLevel.More;
                    index++;
                    continue;
                default:
                    goto doneLeading;
            }
        }
    doneLeading:

        var commandArgs = args[index..];

        // --more is tk-only: no git/dotnet subcommand defines a --more flag, so it's safe
        // to recognize it anywhere in the remaining args, e.g. `tk dotnet test --more
        // --filter X`, and strip it out so it isn't forwarded to the underlying tool.
        // --raw is NOT extended the same way: `git diff --raw`, `git show --raw` and
        // `git log --raw` are real git options, so --raw stays leading-only — once the
        // subcommand has started, a later --raw is left in CommandArgs untouched for the
        // underlying tool to interpret.
        if (Array.IndexOf(commandArgs, "--more") >= 0)
        {
            detailLevel = DetailLevel.More;
            commandArgs = commandArgs.Where(a => a != "--more").ToArray();
        }

        return new CliOptions(raw, detailLevel, commandArgs);
    }
}
