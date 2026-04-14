using System.Text;

namespace Tk.Filters;

/// <summary>Compact find output: strips common path prefix, suppresses permission errors.</summary>
public sealed class FindFilter : IOutputFilter
{
    private readonly DetailLevel _detailLevel;
    private readonly int _maxPaths;
    private readonly int _maxGroups;

    public FindFilter(DetailLevel detailLevel)
    {
        _detailLevel = detailLevel;
        _maxPaths = detailLevel == DetailLevel.More ? 20 : 5;
        _maxGroups = detailLevel == DetailLevel.More ? 6 : 3;
    }

    public string Apply(string raw, int exitCode)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return exitCode == 0 ? "find n=0\n" : raw;

        var lines = raw.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        if (lines.Length == 0)
            return "find n=0\n";

        var paths = new List<string>();
        var errorCount = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith("find:") || line.Contains("Permission denied"))
                errorCount++;
            else
                paths.Add(line);
        }

        var prefix = PathUtils.FindCommonPrefix(paths);
        var stripped = paths.Select(p => PathUtils.StripPrefix(p, prefix)).ToList();
        var topGroups = stripped
            .GroupBy(TopGroupKey)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(_maxGroups)
            .Select(g => $"{g.Key}({g.Count()})")
            .ToList();

        var sb = new StringBuilder();
        sb.Append($"find n={paths.Count}");
        if (errorCount > 0)
            sb.Append($" err={errorCount}");
        sb.AppendLine();

        if (topGroups.Count > 0)
            sb.AppendLine($"top={string.Join(",", topGroups)}");

        if (stripped.Count > 0)
        {
            sb.AppendLine("paths:");
            foreach (var p in stripped.Take(_maxPaths))
                sb.AppendLine($"  {p}");
        }

        var extra = stripped.Count - _maxPaths;
        if (extra > 0)
            sb.AppendLine(Ansi.Dim($"+{extra} more paths"));

        return sb.ToString();
    }

    private static string TopGroupKey(string path)
    {
        var normalized = path.TrimStart('.', '/');
        var slash = normalized.IndexOf('/');
        return slash > 0 ? normalized[..slash] : normalized;
    }
}
