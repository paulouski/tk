using System.Text;
using System.Text.RegularExpressions;

namespace Tk.Filters;

/// <summary>Compact git diff/show: stat summary + truncated hunks.</summary>
public sealed partial class GitDiffFilter : IOutputFilter
{
    private const int MaxHunkLines = 120;
    private const int MaxFileDiffs = 30;

    public string Apply(string raw, int exitCode)
    {
        if (exitCode != 0) return raw;
        if (string.IsNullOrWhiteSpace(raw)) return $"{Ansi.Green("ok")} git diff: no changes\n";

        var lines = raw.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var fileDiffs = new List<FileDiff>();
        FileDiff? current = null;
        var hunkLineCount = 0;

        foreach (var line in lines)
        {
            // New file diff
            if (line.StartsWith("diff --git "))
            {
                current = new FileDiff(ExtractFileName(line));
                fileDiffs.Add(current);
                hunkLineCount = 0;
                continue;
            }

            if (current == null) continue;

            // Binary file
            if (line.StartsWith("Binary files "))
            {
                current.IsBinary = true;
                continue;
            }

            // Hunk header
            if (line.StartsWith("@@"))
            {
                hunkLineCount = 0;
                current.HunkCount++;
                continue;
            }

            // Skip index/mode lines
            if (line.StartsWith("index ") || line.StartsWith("---") || line.StartsWith("+++") ||
                line.StartsWith("old mode") || line.StartsWith("new mode") ||
                line.StartsWith("new file") || line.StartsWith("deleted file") ||
                line.StartsWith("similarity") || line.StartsWith("rename ") ||
                line.StartsWith("copy "))
            {
                if (line.StartsWith("new file")) current.IsNew = true;
                if (line.StartsWith("deleted file")) current.IsDeleted = true;
                continue;
            }

            // Count additions/deletions
            if (line.StartsWith("+")) current.Additions++;
            else if (line.StartsWith("-")) current.Deletions++;

            // Keep hunk lines (limited)
            hunkLineCount++;
            if (hunkLineCount <= MaxHunkLines)
                current.Lines.Add(line);
        }

        var sb = new StringBuilder();
        var totalAdd = fileDiffs.Sum(f => f.Additions);
        var totalDel = fileDiffs.Sum(f => f.Deletions);
        sb.AppendLine($"git diff: {fileDiffs.Count} files, {Ansi.Green($"+{totalAdd}")} {Ansi.Red($"-{totalDel}")}");
        sb.AppendLine(Ansi.Dim("---"));

        foreach (var f in fileDiffs.Take(MaxFileDiffs))
        {
            var tag = f.IsNew ? Ansi.Green(" (new)") : f.IsDeleted ? Ansi.Red(" (deleted)") : f.IsBinary ? Ansi.Dim(" (binary)") : "";
            sb.AppendLine($"{f.Name}{tag}  {Ansi.Green($"+{f.Additions}")} {Ansi.Red($"-{f.Deletions}")}");
        }
        if (fileDiffs.Count > MaxFileDiffs)
            sb.AppendLine(Ansi.Dim($"... +{fileDiffs.Count - MaxFileDiffs} more files"));

        // Show hunks for files with changes (limited)
        var totalLinesShown = 0;
        const int globalMaxLines = 400;
        foreach (var f in fileDiffs.Where(f => f.Lines.Count > 0).Take(MaxFileDiffs))
        {
            if (totalLinesShown >= globalMaxLines) break;

            sb.AppendLine();
            sb.AppendLine(Ansi.Dim($"--- {f.Name} ---"));
            var remaining = globalMaxLines - totalLinesShown;
            foreach (var line in f.Lines.Take(remaining))
            {
                var colored = line.StartsWith("+") ? Ansi.Green(line)
                    : line.StartsWith("-") ? Ansi.Red(line)
                    : line;
                sb.AppendLine(colored);
                totalLinesShown++;
            }
            if (f.Lines.Count > remaining)
                sb.AppendLine(Ansi.Dim($"  ... +{f.Lines.Count - remaining} more lines in this file"));
        }

        return sb.ToString();
    }

    private static string ExtractFileName(string diffLine)
    {
        // "diff --git a/path/file b/path/file" -> "path/file"
        var match = FileNameRe().Match(diffLine);
        return match.Success ? match.Groups[1].Value : diffLine;
    }

    [GeneratedRegex(@"diff --git a/(.+?) b/")]
    private static partial Regex FileNameRe();

    private class FileDiff(string name)
    {
        public string Name { get; } = name;
        public int Additions { get; set; }
        public int Deletions { get; set; }
        public int HunkCount { get; set; }
        public bool IsBinary { get; set; }
        public bool IsNew { get; set; }
        public bool IsDeleted { get; set; }
        public List<string> Lines { get; } = [];
    }
}
