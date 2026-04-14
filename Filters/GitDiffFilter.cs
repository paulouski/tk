using System.Text;
using System.Text.RegularExpressions;

namespace Tk.Filters;

/// <summary>Compact git diff/show: stat summary + truncated hunks.</summary>
public sealed partial class GitDiffFilter : IOutputFilter
{
    private readonly int _maxTopFiles;
    private readonly int _maxPreviewFiles;
    private readonly int _maxPreviewHunks;
    private readonly int _maxLinesPerHunk;
    private readonly int _maxChangedLines;

    public GitDiffFilter(DetailLevel detailLevel)
    {
        var more = detailLevel == DetailLevel.More;
        _maxTopFiles = more ? 6 : 3;
        _maxPreviewFiles = more ? 6 : 3;
        _maxPreviewHunks = more ? 14 : 6;
        _maxLinesPerHunk = more ? 8 : 4;
        _maxChangedLines = more ? 42 : 18;
    }

    public string Apply(string raw, int exitCode)
    {
        if (exitCode != 0) return raw;
        if (string.IsNullOrWhiteSpace(raw)) return "ok diff f=0\n";

        var lines = raw.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var fileDiffs = new List<FileDiff>();
        FileDiff? current = null;
        Hunk? currentHunk = null;

        foreach (var line in lines)
        {
            // New file diff
            if (line.StartsWith("diff --git "))
            {
                current = new FileDiff(ExtractFileName(line));
                fileDiffs.Add(current);
                currentHunk = null;
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
                currentHunk = new Hunk(FormatHunkHeader(current.Name, line));
                current.Hunks.Add(currentHunk);
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
            if (line.StartsWith("+"))
            {
                current.Additions++;
                AddChangedLine(currentHunk, line);
            }
            else if (line.StartsWith("-"))
            {
                current.Deletions++;
                AddChangedLine(currentHunk, line);
            }
        }

        var sb = new StringBuilder();
        var totalAdd = fileDiffs.Sum(f => f.Additions);
        var totalDel = fileDiffs.Sum(f => f.Deletions);
        var totalBinary = fileDiffs.Count(f => f.IsBinary);
        sb.Append($"diff f={fileDiffs.Count} +{totalAdd} -{totalDel}");
        if (totalBinary > 0)
            sb.Append($" bin={totalBinary}");
        sb.AppendLine();

        var ranked = fileDiffs
            .OrderByDescending(f => f.Additions + f.Deletions)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        if (ranked.Count > 0)
        {
            var top = string.Join(",",
                ranked.Take(_maxTopFiles).Select(FormatTopFile));
            sb.AppendLine($"top={top}");
        }

        var previewFiles = ranked
            .Where(f => f.Hunks.Count > 0)
            .Take(_maxPreviewFiles)
            .ToList();

        var hunksShown = 0;
        var changedLinesShown = 0;

        foreach (var file in previewFiles)
        {
            foreach (var hunk in file.Hunks)
            {
                if (hunksShown >= _maxPreviewHunks || changedLinesShown >= _maxChangedLines)
                    break;

                sb.AppendLine(hunk.Header);
                hunksShown++;

                foreach (var line in hunk.ChangedLines)
                {
                    if (changedLinesShown >= _maxChangedLines)
                        break;

                    var colored = line.StartsWith("+") ? Ansi.Green(line) : Ansi.Red(line);
                    sb.AppendLine(colored);
                    changedLinesShown++;
                }
            }

            if (hunksShown >= _maxPreviewHunks || changedLinesShown >= _maxChangedLines)
                break;
        }

        var hiddenFiles = ranked.Count - previewFiles.Count;
        var hiddenHunks = ranked.Sum(f => f.Hunks.Count) - hunksShown;
        if (hiddenFiles > 0 || hiddenHunks > 0)
        {
            var parts = new List<string>();
            if (hiddenFiles > 0) parts.Add($"{hiddenFiles} files");
            if (hiddenHunks > 0) parts.Add($"{hiddenHunks} hunks");
            sb.AppendLine(Ansi.Dim($"+{string.Join(", ", parts)} more"));
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

    [GeneratedRegex(@"^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@")]
    private static partial Regex HunkHeaderRe();

    private void AddChangedLine(Hunk? hunk, string line)
    {
        if (hunk == null || hunk.ChangedLines.Count >= _maxLinesPerHunk)
            return;

        hunk.ChangedLines.Add(TruncateChangedLine(line));
    }

    private static string TruncateChangedLine(string line)
    {
        const int max = 120;
        return line.Length > max ? line[..max] + "..." : line;
    }

    private static string FormatTopFile(FileDiff diff)
    {
        var name = Path.GetFileName(diff.Name);
        var suffix = new StringBuilder();
        if (diff.IsNew) suffix.Append("*new");
        if (diff.IsDeleted)
        {
            if (suffix.Length > 0) suffix.Append('|');
            suffix.Append("del");
        }
        if (diff.IsBinary)
        {
            if (suffix.Length > 0) suffix.Append('|');
            suffix.Append("bin");
        }

        var baseText = $"{name}(+{diff.Additions} -{diff.Deletions})";
        return suffix.Length == 0 ? baseText : $"{baseText}[{suffix}]";
    }

    private static string FormatHunkHeader(string fileName, string rawHeader)
    {
        var match = HunkHeaderRe().Match(rawHeader);
        if (!match.Success)
            return $"@@ {Path.GetFileName(fileName)}";

        var newStart = int.Parse(match.Groups["newStart"].Value);
        var newCount = int.TryParse(match.Groups["newCount"].Value, out var parsed) ? parsed : 1;
        var range = newCount <= 1 ? $"{newStart}" : $"{newStart}-{newStart + newCount - 1}";
        return $"@@ {Path.GetFileName(fileName)} {range}";
    }

    private class FileDiff(string name)
    {
        public string Name { get; } = name;
        public int Additions { get; set; }
        public int Deletions { get; set; }
        public bool IsBinary { get; set; }
        public bool IsNew { get; set; }
        public bool IsDeleted { get; set; }
        public List<Hunk> Hunks { get; } = [];
    }

    private class Hunk(string header)
    {
        public string Header { get; } = header;
        public List<string> ChangedLines { get; } = [];
    }
}
