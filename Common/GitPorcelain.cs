namespace Tk.Common;

public sealed record GitPorcelainEntry(char X, char Y, string Path);

public static class GitPorcelain
{
    public static GitPorcelainEntry? ParseLine(string line)
    {
        if (line.Length < 4)
            return null;

        var x = line[0];
        var y = line[1];
        var pathPart = line[3..].Trim();

        var renameIndex = pathPart.IndexOf(" -> ", StringComparison.Ordinal);
        if (renameIndex >= 0)
            pathPart = pathPart[(renameIndex + 4)..];

        pathPart = pathPart.Trim('"');
        if (string.IsNullOrWhiteSpace(pathPart))
            return null;

        return new GitPorcelainEntry(x, y, pathPart);
    }
}
