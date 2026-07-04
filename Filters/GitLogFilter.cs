using System.Text;
using System.Text.RegularExpressions;
using Tk.Common;

namespace Tk.Filters;

/// <summary>Compact git log: one line per commit, strip verbose metadata.</summary>
public sealed partial class GitLogFilter : IOutputFilter
{
    private const int MaxCommits = 50;

    public string Apply(string raw, int exitCode) => Apply(raw, exitCode, new UnitLedger());

    public string Apply(string raw, int exitCode, UnitLedger ledger)
    {
        var totalLines = HiddenLinesFooter.CountLines(raw);
        if (exitCode != 0)
        {
            ledger.Keep(totalLines);
            return raw;
        }
        if (string.IsNullOrWhiteSpace(raw))
        {
            ledger.Hide(totalLines);
            return raw;
        }

        var rawLines = raw.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        // Drop the trailing split artifact from a final '\n' — it isn't a physical line.
        var lines = rawLines.Length > 0 && rawLines[^1] == string.Empty ? rawLines[..^1] : rawLines;

        // If already oneline format (short hashes, no "commit" prefix), pass through with limit
        if (lines.Length > 0 && !lines[0].StartsWith("commit "))
            return TruncateLines(raw, MaxCommits, ledger);

        // Parse full format into compact
        var commits = new List<string>();
        var commitLineCounts = new List<int>();
        var currentLineCount = 0;
        string? currentHash = null;
        string? currentAuthor = null;
        string? currentDate = null;
        var currentMessage = new StringBuilder();
        var unrecognizedBeforeFirstCommit = 0;

        foreach (var line in lines)
        {
            var commitMatch = CommitRe().Match(line);
            if (commitMatch.Success)
            {
                if (currentHash != null)
                {
                    commits.Add(FormatCommit(currentHash, currentAuthor, currentDate, currentMessage.ToString().Trim()));
                    commitLineCounts.Add(currentLineCount);
                }

                currentHash = commitMatch.Groups[1].Value[..Math.Min(7, commitMatch.Groups[1].Value.Length)];
                currentAuthor = null;
                currentDate = null;
                currentMessage.Clear();
                currentLineCount = 1;
                continue;
            }

            if (currentHash == null)
            {
                // Content before any "commit " header was seen — not a recognized git-log shape.
                unrecognizedBeforeFirstCommit++;
                continue;
            }

            if (line.StartsWith("Author:"))
            {
                currentAuthor = line["Author:".Length..].Trim();
                // Strip email
                var emailIdx = currentAuthor.IndexOf('<');
                if (emailIdx > 0) currentAuthor = currentAuthor[..emailIdx].Trim();
                currentLineCount++;
                continue;
            }

            if (line.StartsWith("Date:"))
            {
                currentDate = line["Date:".Length..].Trim();
                currentLineCount++;
                continue;
            }

            if (line.StartsWith("Merge:") || line.StartsWith("    Co-Authored-By:"))
            {
                currentLineCount++;
                continue;
            }

            if (line.StartsWith("    "))
            {
                var msg = line.Trim();
                if (!string.IsNullOrEmpty(msg))
                    currentMessage.AppendLine(msg);
                currentLineCount++;
                continue;
            }

            // Blank line inside a commit block (spacer between header/body/next commit).
            currentLineCount++;
        }

        // Last commit
        if (currentHash != null)
        {
            commits.Add(FormatCommit(currentHash, currentAuthor, currentDate, currentMessage.ToString().Trim()));
            commitLineCounts.Add(currentLineCount);
        }

        if (commits.Count == 0)
        {
            // Nothing ever parsed as a real commit — treat the whole input as opaque text via
            // the same passthrough/cap TruncateLines uses for oneline input, rather than
            // double-classifying lines already tallied as unrecognizedBeforeFirstCommit above.
            return TruncateLines(raw, MaxCommits, ledger);
        }

        ledger.Unparsed(unrecognizedBeforeFirstCommit);

        var sb = new StringBuilder();
        var shown = Math.Min(MaxCommits, commits.Count);
        for (var i = 0; i < shown; i++)
            sb.AppendLine(commits[i]);
        ledger.Keep(commitLineCounts.Take(shown).Sum());
        if (commits.Count > MaxCommits)
        {
            sb.AppendLine(Ansi.Dim($"... +{commits.Count - MaxCommits} more commits"));
            ledger.Summarize(commitLineCounts.Skip(shown).Sum());
        }

        return sb.ToString();
    }

    private static string FormatCommit(string hash, string? author, string? date, string message)
    {
        // Take first line of message only
        var firstLine = message.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "(no message)";
        if (firstLine.Length > 100) firstLine = firstLine[..100] + "...";
        return $"{Ansi.Yellow(hash)} {firstLine}";
    }

    private static string TruncateLines(string text, int max, UnitLedger ledger)
    {
        var lines = text.Split('\n');
        var totalLines = lines.Length > 0 && lines[^1] == string.Empty ? lines.Length - 1 : lines.Length;

        if (lines.Length <= max)
        {
            ledger.Keep(totalLines);
            return text;
        }

        ledger.Keep(Math.Min(max, totalLines));
        ledger.Summarize(Math.Max(0, totalLines - max));
        return string.Join('\n', lines.Take(max)) + $"\n... +{lines.Length - max} more lines\n";
    }

    [GeneratedRegex(@"^commit\s+([0-9a-f]{7,40})")]
    private static partial Regex CommitRe();
}
