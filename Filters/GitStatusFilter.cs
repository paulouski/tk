using System.Text;
using System.Text.RegularExpressions;

namespace Tk.Filters;

/// <summary>Compact git status with a count-first agent-friendly format.</summary>
public sealed partial class GitStatusFilter : IOutputFilter
{
    private readonly DetailLevel _detailLevel;
    private readonly int _maxTopPaths;

    public GitStatusFilter(DetailLevel detailLevel)
    {
        _detailLevel = detailLevel;
        _maxTopPaths = detailLevel == DetailLevel.More ? 12 : 5;
    }

    public string Apply(string raw, int exitCode)
    {
        if (exitCode != 0) return raw;

        var lines = raw.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

        if (lines.Length == 0)
            return "ok status st=0 mod=0 untr=0\n";

        if (LooksLikePorcelain(lines))
            return FormatPorcelain(lines);

        var staged = new List<string>();
        var modified = new List<string>();
        var untracked = new List<string>();
        string? branch = null;

        var section = Section.None;

        foreach (var line in lines)
        {
            if (line.StartsWith("On branch "))
            {
                branch = line["On branch ".Length..].Trim();
                continue;
            }

            if (line.StartsWith("Changes to be committed:"))
            {
                section = Section.Staged;
                continue;
            }
            if (line.StartsWith("Changes not staged"))
            {
                section = Section.Modified;
                continue;
            }
            if (line.StartsWith("Untracked files:"))
            {
                section = Section.Untracked;
                continue;
            }

            if (line.StartsWith("Your branch ") || line.StartsWith("  (use ") ||
                line.StartsWith("no changes added"))
                continue;

            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (!line.StartsWith("\t")) continue;

            switch (section)
            {
                case Section.Staged:
                    staged.Add(trimmed);
                    break;
                case Section.Modified:
                    modified.Add(trimmed);
                    break;
                case Section.Untracked:
                    untracked.Add(trimmed);
                    break;
            }
        }

        return FormatSummary(branch, staged, modified, untracked);
    }

    private string FormatSummary(string? branch, List<string> staged, List<string> modified, List<string> untracked)
    {
        var clean = staged.Count == 0 && modified.Count == 0 && untracked.Count == 0;
        var sb = new StringBuilder();
        sb.Append(clean ? "ok status" : "status");
        sb.Append($" st={staged.Count} mod={modified.Count} untr={untracked.Count}");
        if (!string.IsNullOrWhiteSpace(branch))
            sb.Append($" br={SanitizeBranch(branch)}");
        sb.AppendLine();

        var top = new List<string>();
        top.AddRange(staged.Select(p => $"s:{p}"));
        top.AddRange(modified.Select(p => $"m:{p}"));
        top.AddRange(untracked.Select(p => $"u:{p}"));
        if (top.Count > 0)
            sb.AppendLine($"top={string.Join(",", top.Take(_maxTopPaths))}");

        if (_detailLevel == DetailLevel.More)
        {
            AppendSection(sb, "staged", staged);
            AppendSection(sb, "modified", modified);
            AppendSection(sb, "untracked", untracked);
        }

        return sb.ToString();
    }

    private static bool LooksLikePorcelain(string[] lines) =>
        lines.Any(line => line.StartsWith("## "))
        || lines.All(line => line.Length >= 3 && PorcelainStatusRe().IsMatch(line));

    private string FormatPorcelain(string[] lines)
    {
        string? branch = null;
        var staged = new List<string>();
        var modified = new List<string>();
        var untracked = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("## "))
            {
                branch = line["## ".Length..].Trim();
                continue;
            }

            if (line.Length < 4)
                continue;

            var x = line[0];
            var y = line[1];
            var path = line[3..].Trim();
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (x == '?' && y == '?')
            {
                untracked.Add(path);
                continue;
            }

            if (x != ' ' && x != '?')
                staged.Add(path);
            if (y != ' ' && y != '?')
                modified.Add(path);
        }

        return FormatSummary(branch, staged, modified, untracked);
    }

    private static void AppendSection(StringBuilder sb, string title, List<string> items)
    {
        if (items.Count == 0)
            return;

        sb.AppendLine($"{title}:");
        foreach (var item in items)
            sb.AppendLine($"  {item}");
    }

    private static string SanitizeBranch(string branch)
    {
        var branchOnly = branch.Split("...", 2, StringSplitOptions.None)[0].Trim();
        return branchOnly.Replace(' ', '_');
    }

    [GeneratedRegex(@"^(?:\?\?|!!|[ MARCUD][ MARCUD])\s")]
    private static partial Regex PorcelainStatusRe();

    private enum Section { None, Staged, Modified, Untracked }
}
