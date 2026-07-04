using System.Text;
using Tk.Common;

namespace Tk.Filters;

/// <summary>Ultra-compact filter for git operations: add, commit, push, pull, etc.</summary>
public sealed class GitCompactFilter : IOutputFilter
{
    public string Apply(string raw, int exitCode) => Apply(raw, exitCode, new UnitLedger());

    public string Apply(string raw, int exitCode, UnitLedger ledger)
    {
        var rawLines = raw.Split('\n');
        // Trailing empty element from a final '\n' is not a physical line.
        var totalLines = rawLines.Length > 0 && rawLines[^1] == string.Empty
            ? rawLines.Length - 1
            : rawLines.Length;

        if (string.IsNullOrWhiteSpace(raw) && exitCode == 0)
        {
            ledger.Hide(totalLines);
            return $"{Ansi.Green("ok")}\n";
        }

        var blankCount = 0;
        var lines = new List<string>();
        foreach (var l in rawLines.Select(l => l.TrimEnd('\r')))
        {
            if (string.IsNullOrWhiteSpace(l))
                blankCount++;
            else
                lines.Add(l);
        }
        // The trailing split artifact (if any) was already excluded from totalLines above but is
        // still present in rawLines/blankCount when raw ends with '\n' — correct for it.
        if (rawLines.Length > 0 && rawLines[^1] == string.Empty)
            blankCount--;

        if (lines.Count == 0 && exitCode == 0)
        {
            ledger.Hide(blankCount);
            return $"{Ansi.Green("ok")}\n";
        }

        ledger.Hide(blankCount);

        // For short outputs (< 5 lines), pass through
        if (lines.Count <= 5)
        {
            ledger.Keep(lines.Count);
            return raw;
        }

        // For longer outputs, keep first 3 and last 2 lines; the rest are summarized by the
        // "... (N lines omitted)" line.
        var sb = new StringBuilder();
        foreach (var line in lines.Take(3))
            sb.AppendLine(line);
        ledger.Keep(3);
        sb.AppendLine(Ansi.Dim($"... ({lines.Count - 5} lines omitted)"));
        ledger.Summarize(lines.Count - 5);
        foreach (var line in lines.TakeLast(2))
            sb.AppendLine(line);
        ledger.Keep(2);

        return sb.ToString();
    }
}
