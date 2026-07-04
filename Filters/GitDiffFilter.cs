using System.Text;
using System.Text.RegularExpressions;
using Tk.Common;

namespace Tk.Filters;

/// <summary>Compact git diff/show: stat summary + faithful hunk output (all changed lines verbatim).
/// Pass summary:true for the old lossy preview behaviour (capped changed lines).</summary>
public sealed partial class GitDiffFilter : IOutputFilter
{
    private readonly bool _summary;
    private readonly bool _isShow;

    // Summary-mode caps (old behaviour, now opt-in)
    private readonly int _maxPreviewFiles;
    private readonly int _maxPreviewHunks;
    private readonly int _maxLinesPerHunk;
    private readonly int _maxChangedLines;

    public GitDiffFilter(DetailLevel detailLevel, bool summary = false, bool isShow = false)
    {
        _summary = summary;
        _isShow = isShow;
        var more = detailLevel == DetailLevel.More;
        // top= always lists ALL files; these control the hunk preview in summary mode only
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

        // For git show: collect everything before the first "diff --git" line as the commit header.
        string? commitHeader = null;
        int diffStartIdx = 0;
        if (_isShow)
        {
            var firstDiff = Array.FindIndex(lines, l => l.StartsWith("diff --git "));
            if (firstDiff > 0)
            {
                commitHeader = string.Join("\n", lines[..firstDiff]).TrimEnd();
                diffStartIdx = firstDiff;
            }
        }

        var fileDiffs = new List<FileDiff>();
        FileDiff? current = null;
        Hunk? currentHunk = null;

        foreach (var line in lines[diffStartIdx..])
        {
            // New file diff — "diff --git" (regular) or "diff --cc" (combined format for
            // unmerged/conflicted paths, e.g. mid-rebase or mid-merge).
            if (line.StartsWith("diff --git ") || line.StartsWith("diff --cc "))
            {
                current = new FileDiff(ExtractFileName(line)) { IsConflict = line.StartsWith("diff --cc ") };
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
                if (line.StartsWith("rename to ")) current.RenamedTo = line["rename to ".Length..].Trim();
                continue;
            }

            // Count additions/deletions; capture context lines around changes
            if (current.IsConflict)
            {
                // Combined diff format: one prefix char per parent (>=2 chars); '+'/'-' anywhere
                // in the prefix marks the line changed relative to some parent.
                var prefix = line.Length >= 2 ? line[..2] : line;
                if (prefix.Contains('+'))
                {
                    current.Additions++;
                    AddHunkLine(currentHunk, line, isChanged: true);
                }
                else if (prefix.Contains('-'))
                {
                    current.Deletions++;
                    AddHunkLine(currentHunk, line, isChanged: true);
                }
                else
                {
                    AddHunkLine(currentHunk, line, isChanged: false);
                }
            }
            else if (line.StartsWith("+"))
            {
                current.Additions++;
                AddHunkLine(currentHunk, line, isChanged: true);
            }
            else if (line.StartsWith("-"))
            {
                current.Deletions++;
                AddHunkLine(currentHunk, line, isChanged: true);
            }
            else if (line.StartsWith(" "))
            {
                // Context line — include for interpretability
                AddHunkLine(currentHunk, line, isChanged: false);
            }
        }

        var sb = new StringBuilder();

        // Emit commit header (git show only)
        if (commitHeader != null)
        {
            sb.AppendLine(commitHeader);
        }

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
            // All changed files are listed — the set is always complete
            var top = string.Join(",", ranked.Select(FormatTopFile));
            sb.AppendLine($"top={top}");
        }

        if (_summary)
            EmitSummaryHunks(sb, ranked);
        else
            EmitFaithfulHunks(sb, ranked);

        return sb.ToString();
    }

    // Faithful mode: emit ALL hunks from ALL files; every changed line verbatim.
    private static void EmitFaithfulHunks(StringBuilder sb, List<FileDiff> ranked)
    {
        foreach (var file in ranked)
        {
            foreach (var hunk in file.Hunks)
            {
                sb.AppendLine(hunk.Header);
                foreach (var entry in hunk.Lines)
                {
                    if (entry.IsChanged)
                    {
                        var colored = entry.Text.StartsWith("+") ? Ansi.Green(entry.Text) : Ansi.Red(entry.Text);
                        sb.AppendLine(colored);
                    }
                    else
                    {
                        sb.AppendLine(Ansi.Dim(entry.Text));
                    }
                }
            }
        }
    }

    // Summary mode: old capped/lossy preview behaviour.
    private void EmitSummaryHunks(StringBuilder sb, List<FileDiff> ranked)
    {
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

                foreach (var entry in hunk.Lines)
                {
                    if (entry.IsChanged && changedLinesShown >= _maxChangedLines)
                        break;

                    if (entry.IsChanged)
                    {
                        var colored = entry.Text.StartsWith("+") ? Ansi.Green(entry.Text) : Ansi.Red(entry.Text);
                        sb.AppendLine(colored);
                        changedLinesShown++;
                    }
                    else
                    {
                        // Context line — show as-is (dim)
                        sb.AppendLine(Ansi.Dim(entry.Text));
                    }
                }
            }

            if (hunksShown >= _maxPreviewHunks || changedLinesShown >= _maxChangedLines)
                break;
        }

        // All files are in top=; report only hidden hunk previews
        var hiddenHunks = ranked.Sum(f => f.Hunks.Count) - hunksShown;
        if (hiddenHunks > 0)
            sb.AppendLine(Ansi.Dim($"+{hiddenHunks} hunks more (all files listed above)"));
    }

    private static string ExtractFileName(string diffLine)
    {
        // "diff --cc path/file" (combined/conflict format) -> "path/file"
        if (diffLine.StartsWith("diff --cc "))
            return diffLine["diff --cc ".Length..].Trim();

        // "diff --git a/path/file b/path/file" -> "path/file"
        var match = FileNameRe().Match(diffLine);
        return match.Success ? match.Groups[1].Value : diffLine;
    }

    [GeneratedRegex(@"diff --git a/(.+?) b/")]
    private static partial Regex FileNameRe();

    [GeneratedRegex(@"^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@")]
    private static partial Regex HunkHeaderRe();

    // Combined diff hunk header (git diff --cc), e.g. "@@@ -1,3 -1,3 +1,4 @@@": N old ranges
    // (one per parent), then a single new range.
    [GeneratedRegex(@"^@{2,}\s(?:-\d+(?:,\d+)?\s)+\+(?<newStart>\d+)(?:,(?<newCount>\d+))?\s@{2,}")]
    private static partial Regex CombinedHunkHeaderRe();

    private void AddHunkLine(Hunk? hunk, string line, bool isChanged)
    {
        if (hunk == null) return;
        if (_summary)
        {
            // In summary mode: cap changed lines per hunk
            if (isChanged && hunk.ChangedLineCount >= _maxLinesPerHunk) return;
            hunk.Lines.Add(new HunkLine(TruncateChangedLine(line), isChanged));
        }
        else
        {
            // Faithful mode: no caps, no truncation
            hunk.Lines.Add(new HunkLine(line, isChanged));
        }
        if (isChanged) hunk.ChangedLineCount++;
    }

    private static string TruncateChangedLine(string line)
    {
        const int max = 120;
        return line.Length > max ? line[..max] + "..." : line;
    }

    private static string FormatTopFile(FileDiff diff)
    {
        var name = Path.GetFileName(diff.Name);
        if (diff.RenamedTo is not null)
            name = $"{name}->{Path.GetFileName(diff.RenamedTo)}";

        var suffix = new StringBuilder();
        if (diff.IsNew) suffix.Append("*new");
        if (diff.IsDeleted)
        {
            if (suffix.Length > 0) suffix.Append('|');
            suffix.Append("del");
        }
        if (diff.RenamedTo is not null)
        {
            if (suffix.Length > 0) suffix.Append('|');
            suffix.Append("ren");
        }
        if (diff.IsConflict)
        {
            if (suffix.Length > 0) suffix.Append('|');
            suffix.Append("conflict");
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
            match = CombinedHunkHeaderRe().Match(rawHeader);
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
        public bool IsConflict { get; set; }
        public string? RenamedTo { get; set; }
        public bool IsNew { get; set; }
        public bool IsDeleted { get; set; }
        public List<Hunk> Hunks { get; } = [];
    }

    private class Hunk(string header)
    {
        public string Header { get; } = header;
        public List<HunkLine> Lines { get; } = [];
        public int ChangedLineCount { get; set; }
    }

    private class HunkLine(string text, bool isChanged)
    {
        public string Text { get; } = text;
        public bool IsChanged { get; } = isChanged;
    }
}
