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
                    return new CliOptions(raw, detailLevel, args[index..]);
            }
        }

        return new CliOptions(raw, detailLevel, []);
    }
}
