namespace Tk.Common;

internal static class PathUtils
{
    public static string FindCommonPrefix(IEnumerable<string> paths)
    {
        string? prefix = null;

        foreach (var path in paths)
        {
            var normalizedPath = Normalize(path);
            var dir = Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/');
            if (dir == null) return "";

            if (prefix == null)
            {
                prefix = dir;
                continue;
            }

            while (!string.IsNullOrEmpty(prefix) &&
                   !normalizedPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                prefix = Path.GetDirectoryName(prefix)?.Replace('\\', '/');
                if (prefix == null) return "";
            }
        }

        return string.IsNullOrEmpty(prefix) ? "" : prefix + "/";
    }

    public static string StripPrefix(string path, string prefix) =>
        StripPrefixNormalized(Normalize(path), prefix);

    private static string StripPrefixNormalized(string path, string prefix) =>
        !string.IsNullOrEmpty(prefix) && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? path[prefix.Length..]
            : path;

    private static string Normalize(string path) => path.Replace('\\', '/');
}
