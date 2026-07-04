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

    private readonly Rule[] _lineRules;

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
        _lineRules = BuildLineRules();
    }

    // Per-line diff-shape classification, in precedence order (mirrors the original if/else
    // ladder exactly). The first matching rule wins; rule 12 is a mandatory catch-all so every
    // line is always classified. Order is load-bearing in several places — see per-rule notes.
    private readonly record struct Rule(Func<ParseState, string, bool> Match, Action<ParseState, string, UnitLedger> Apply);

    private sealed class ParseState
    {
        public FileDiff? Current;
        public Hunk? CurrentHunk;
        public List<FileDiff> FileDiffs { get; } = [];
    }

    private Rule[] BuildLineRules() => new Rule[]
    {
        // 1. New file diff header — "diff --git" (regular) or "diff --cc" (combined format for
        //    unmerged/conflicted paths, e.g. mid-rebase or mid-merge). Must run first: it starts
        //    a new FileDiff that every rule below depends on.
        new(
            Match: (_, line) => line.StartsWith("diff --git ") || line.StartsWith("diff --cc "),
            Apply: (state, line, ledger) =>
            {
                state.Current = new FileDiff(ExtractFileName(line)) { IsConflict = line.StartsWith("diff --cc ") };
                state.FileDiffs.Add(state.Current);
                state.CurrentHunk = null;
                // Not shown verbatim — represented by this file's entry in top=.
                ledger.Summarize();
            }),

        // 2. Guard: no file diff header has been seen yet, so nothing below applies — alien
        //    input (e.g. a `git show` of a commit whose body never reaches a diff at all). Must
        //    run before every shape rule below, since none of them are meaningful without
        //    `Current`.
        new(
            Match: (state, _) => state.Current == null,
            Apply: (_, _, ledger) => ledger.Unparsed()),

        // 3. Binary file marker.
        new(
            Match: (_, line) => line.StartsWith("Binary files "),
            Apply: (state, _, ledger) =>
            {
                state.Current!.IsBinary = true;
                // Represented by this file's bin= contribution, not shown verbatim.
                ledger.Summarize();
            }),

        // 4. Hunk header ("@@ ... @@", or combined "@@@ ... @@@" for conflicts).
        new(
            Match: (_, line) => line.StartsWith("@@"),
            Apply: (state, line, _) =>
            {
                state.CurrentHunk = new Hunk(FormatHunkHeader(state.Current!.Name, line));
                state.Current!.Hunks.Add(state.CurrentHunk);
                // Kept-or-summarized decision deferred to emit time (summary mode may cap it).
            }),

        // 5. Structural metadata lines — recognized git-diff scaffolding, intentionally never
        //    rendered. Must run before the content-line rules below, since e.g. "---"/"+++"
        //    would otherwise be mistaken for a deletion/addition line.
        new(
            Match: (_, line) => line.StartsWith("index ") || line.StartsWith("---") || line.StartsWith("+++") ||
                                 line.StartsWith("old mode") || line.StartsWith("new mode") ||
                                 line.StartsWith("new file") || line.StartsWith("deleted file") ||
                                 line.StartsWith("similarity") || line.StartsWith("rename ") ||
                                 line.StartsWith("copy "),
            Apply: (state, line, ledger) =>
            {
                if (line.StartsWith("new file")) state.Current!.IsNew = true;
                if (line.StartsWith("deleted file")) state.Current!.IsDeleted = true;
                if (line.StartsWith("rename to ")) state.Current!.RenamedTo = line["rename to ".Length..].Trim();
                ledger.Hide();
            }),

        // 6-8. Combined-diff (conflict) content lines: one prefix char per parent (>=2 chars);
        //    '+'/'-' anywhere in the prefix marks the line changed relative to some parent. Must
        //    run before the plain +/-/space rules below — a conflict line's 2-char prefix (e.g.
        //    "+ ") would otherwise be misread using plain unified-diff semantics.
        new(
            Match: (state, line) => state.Current!.IsConflict && Prefix(line).Contains('+'),
            Apply: (state, line, ledger) =>
            {
                state.Current!.Additions++;
                AddHunkLine(state.CurrentHunk, line, isChanged: true, ledger);
            }),
        new(
            Match: (state, line) => state.Current!.IsConflict && Prefix(line).Contains('-'),
            Apply: (state, line, ledger) =>
            {
                state.Current!.Deletions++;
                AddHunkLine(state.CurrentHunk, line, isChanged: true, ledger);
            }),
        new(
            Match: (state, _) => state.Current!.IsConflict,
            Apply: (state, line, ledger) => AddHunkLine(state.CurrentHunk, line, isChanged: false, ledger)),

        // 9-11. Plain unified-diff content lines.
        new(
            Match: (_, line) => line.StartsWith("+"),
            Apply: (state, line, ledger) =>
            {
                state.Current!.Additions++;
                AddHunkLine(state.CurrentHunk, line, isChanged: true, ledger);
            }),
        new(
            Match: (_, line) => line.StartsWith("-"),
            Apply: (state, line, ledger) =>
            {
                state.Current!.Deletions++;
                AddHunkLine(state.CurrentHunk, line, isChanged: true, ledger);
            }),
        new(
            Match: (_, line) => line.StartsWith(" "),
            // Context line — include for interpretability
            Apply: (state, line, ledger) => AddHunkLine(state.CurrentHunk, line, isChanged: false, ledger)),

        // 12. Mandatory catch-all — doesn't match any recognized diff line shape (e.g. "\ No
        //     newline at end of file") — alien input.
        new(
            Match: (_, _) => true,
            Apply: (_, _, ledger) => ledger.Unparsed()),
    };

    private static string Prefix(string line) => line.Length >= 2 ? line[..2] : line;

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
            return "ok diff f=0\n";
        }

        var rawLines = raw.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        // Drop the trailing split artifact from a final '\n' — it isn't a physical line.
        var lines = rawLines.Length > 0 && rawLines[^1] == string.Empty ? rawLines[..^1] : rawLines;

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

        if (commitHeader != null)
            ledger.Keep(diffStartIdx);

        var state = new ParseState();

        foreach (var line in lines[diffStartIdx..])
        {
            foreach (var rule in _lineRules)
            {
                if (!rule.Match(state, line)) continue;
                rule.Apply(state, line, ledger);
                break;
            }
        }

        var fileDiffs = state.FileDiffs;

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
            EmitSummaryHunks(sb, ranked, ledger);
        else
            EmitFaithfulHunks(sb, ranked, ledger);

        return sb.ToString();
    }

    // Faithful mode: emit ALL hunks from ALL files; every changed line verbatim.
    private static void EmitFaithfulHunks(StringBuilder sb, List<FileDiff> ranked, UnitLedger ledger)
    {
        foreach (var file in ranked)
        {
            foreach (var hunk in file.Hunks)
            {
                sb.AppendLine(hunk.Header);
                ledger.Keep();
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
                    ledger.Keep();
                }
            }
        }
    }

    // Summary mode: old capped/lossy preview behaviour.
    private void EmitSummaryHunks(StringBuilder sb, List<FileDiff> ranked, UnitLedger ledger)
    {
        var totalHunks = ranked.Sum(f => f.Hunks.Count);
        var totalContentLines = ranked.Sum(f => f.Hunks.Sum(h => h.Lines.Count));
        var hunksShownCount = 0;
        var contentLinesShownCount = 0;

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
                hunksShownCount++;

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
                    contentLinesShownCount++;
                }
            }

            if (hunksShown >= _maxPreviewHunks || changedLinesShown >= _maxChangedLines)
                break;
        }

        // All files are in top=; report only hidden hunk previews
        var hiddenHunks = ranked.Sum(f => f.Hunks.Count) - hunksShown;
        if (hiddenHunks > 0)
            sb.AppendLine(Ansi.Dim($"+{hiddenHunks} hunks more (all files listed above)"));

        ledger.Keep(hunksShownCount + contentLinesShownCount);
        ledger.Summarize((totalHunks - hunksShownCount) + (totalContentLines - contentLinesShownCount));
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

    private void AddHunkLine(Hunk? hunk, string line, bool isChanged, UnitLedger ledger)
    {
        if (hunk == null)
        {
            // A content line arrived with no active hunk — alien input (shouldn't happen for
            // well-formed diffs, since a hunk header always precedes content lines).
            ledger.Unparsed();
            return;
        }
        if (_summary)
        {
            // In summary mode: cap changed lines per hunk
            if (isChanged && hunk.ChangedLineCount >= _maxLinesPerHunk)
            {
                // Dropped at construction time — never eligible to be shown; represented by the
                // file's aggregate (+X -Y) stats, which were already incremented by the caller.
                ledger.Summarize();
                return;
            }
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
