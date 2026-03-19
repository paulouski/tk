using System.Text;

namespace Tk.Filters;

/// <summary>Ultra-compact filter for git operations: add, commit, push, pull, etc.</summary>
public sealed class GitCompactFilter : IOutputFilter
{
    public string Apply(string raw, int exitCode)
    {
        if (string.IsNullOrWhiteSpace(raw) && exitCode == 0)
            return $"{Ansi.Green("ok")}\n";

        var lines = raw.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        if (lines.Length == 0 && exitCode == 0)
            return $"{Ansi.Green("ok")}\n";

        // For short outputs (< 5 lines), pass through
        if (lines.Length <= 5)
            return raw;

        // For longer outputs, keep first 3 and last 2 lines
        var sb = new StringBuilder();
        foreach (var line in lines.Take(3))
            sb.AppendLine(line);
        if (lines.Length > 5)
            sb.AppendLine(Ansi.Dim($"... ({lines.Length - 5} lines omitted)"));
        foreach (var line in lines.TakeLast(2))
            sb.AppendLine(line);

        return sb.ToString();
    }
}
