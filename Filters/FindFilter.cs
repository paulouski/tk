using System.Text;
using Tk.Common;

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

    public string Apply(string raw, int exitCode) => Apply(raw, exitCode, new UnitLedger());

    public string Apply(string raw, int exitCode, UnitLedger ledger)
    {
        var totalLines = HiddenLinesFooter.CountLines(raw);

        if (string.IsNullOrWhiteSpace(raw))
        {
            ledger.Hide(totalLines);
            return exitCode == 0 ? "find n=0\n" : raw;
        }

        var rawLines = raw.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var blankCount = 0;
        var lines = new List<string>();
        foreach (var l in rawLines)
        {
            if (string.IsNullOrWhiteSpace(l))
                blankCount++;
            else
                lines.Add(l);
        }
        // raw.Split('\n') may include a trailing empty artifact when raw ends with '\n' — that
        // artifact isn't a physical line (totalLines already excludes it), so don't double-hide it.
        if (rawLines.Length > 0 && rawLines[^1] == string.Empty)
            blankCount--;

        if (lines.Count == 0)
        {
            ledger.Hide(blankCount);
            return "find n=0\n";
        }

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

        // Blank lines: recognized formatting, intentionally omitted.
        ledger.Hide(blankCount);
        // Error lines (find:/Permission denied): recognized, represented by the err= count.
        ledger.Summarize(errorCount);
        // Shown paths: rendered verbatim (after prefix-stripping).
        ledger.Keep(Math.Min(_maxPaths, stripped.Count));
        // Paths beyond the cap: represented by the "+N more paths" line.
        if (extra > 0)
            ledger.Summarize(extra);

        return sb.ToString();
    }

    private static string TopGroupKey(string path)
    {
        var normalized = path.TrimStart('.', '/');
        var slash = normalized.IndexOf('/');
        return slash > 0 ? normalized[..slash] : normalized;
    }
}
