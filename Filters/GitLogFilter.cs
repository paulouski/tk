using System.Text;
using System.Text.RegularExpressions;

namespace Tk.Filters;

/// <summary>Compact git log: one line per commit, strip verbose metadata.</summary>
public sealed partial class GitLogFilter : IOutputFilter
{
    private const int MaxCommits = 50;

    public string Apply(string raw, int exitCode)
    {
        if (exitCode != 0) return raw;
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        var lines = raw.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        // If already oneline format (short hashes, no "commit" prefix), pass through with limit
        if (lines.Length > 0 && !lines[0].StartsWith("commit "))
            return TruncateLines(raw, MaxCommits);

        // Parse full format into compact
        var commits = new List<string>();
        string? currentHash = null;
        string? currentAuthor = null;
        string? currentDate = null;
        var currentMessage = new StringBuilder();

        foreach (var line in lines)
        {
            var commitMatch = CommitRe().Match(line);
            if (commitMatch.Success)
            {
                if (currentHash != null)
                    commits.Add(FormatCommit(currentHash, currentAuthor, currentDate, currentMessage.ToString().Trim()));

                currentHash = commitMatch.Groups[1].Value[..Math.Min(7, commitMatch.Groups[1].Value.Length)];
                currentAuthor = null;
                currentDate = null;
                currentMessage.Clear();
                continue;
            }

            if (line.StartsWith("Author:"))
            {
                currentAuthor = line["Author:".Length..].Trim();
                // Strip email
                var emailIdx = currentAuthor.IndexOf('<');
                if (emailIdx > 0) currentAuthor = currentAuthor[..emailIdx].Trim();
                continue;
            }

            if (line.StartsWith("Date:"))
            {
                currentDate = line["Date:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("Merge:") || line.StartsWith("    Co-Authored-By:"))
                continue;

            if (line.StartsWith("    "))
            {
                var msg = line.Trim();
                if (!string.IsNullOrEmpty(msg))
                    currentMessage.AppendLine(msg);
            }
        }

        // Last commit
        if (currentHash != null)
            commits.Add(FormatCommit(currentHash, currentAuthor, currentDate, currentMessage.ToString().Trim()));

        if (commits.Count == 0)
            return TruncateLines(raw, MaxCommits);

        var sb = new StringBuilder();
        foreach (var c in commits.Take(MaxCommits))
            sb.AppendLine(c);
        if (commits.Count > MaxCommits)
            sb.AppendLine(Ansi.Dim($"... +{commits.Count - MaxCommits} more commits"));

        return sb.ToString();
    }

    private static string FormatCommit(string hash, string? author, string? date, string message)
    {
        // Take first line of message only
        var firstLine = message.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "(no message)";
        if (firstLine.Length > 100) firstLine = firstLine[..100] + "...";
        return $"{Ansi.Yellow(hash)} {firstLine}";
    }

    private static string TruncateLines(string text, int max)
    {
        var lines = text.Split('\n');
        if (lines.Length <= max) return text;
        return string.Join('\n', lines.Take(max)) + $"\n... +{lines.Length - max} more lines\n";
    }

    [GeneratedRegex(@"^commit\s+([0-9a-f]{7,40})")]
    private static partial Regex CommitRe();
}
