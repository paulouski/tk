using System.Text;
using System.Text.RegularExpressions;
using Tk.Common;

namespace Tk.Filters;

/// <summary>Compact git status with a count-first agent-friendly format.</summary>
public sealed partial class GitStatusFilter : IOutputFilter
{
    private readonly bool _unityMode;

    public GitStatusFilter(DetailLevel detailLevel, bool unityMode = false)
    {
        _unityMode = unityMode;
    }

    public string Apply(string raw, int exitCode) => Apply(raw, exitCode, stateRaw: null);

    public string Apply(string raw, int exitCode, string? stateRaw)
    {
        if (exitCode != 0) return raw;

        var lines = raw.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

        if (lines.Length == 0)
            return "ok status st=0 mod=0 untr=0\n";

        if (LooksLikePorcelain(lines))
            return FormatPorcelain(lines, stateRaw);

        var staged = new List<string>();
        var modified = new List<string>();
        var untracked = new List<string>();
        var deleted = new List<string>();
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

            if (trimmed.StartsWith("deleted:", StringComparison.Ordinal) &&
                section is Section.Staged or Section.Modified)
            {
                deleted.Add(trimmed);
                continue;
            }

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

        return FormatSummary(branch, staged, modified, untracked, deleted, stateRaw: stateRaw);
    }

    private string FormatSummary(string? branch, List<string> staged, List<string> modified, List<string> untracked, List<string> deleted, List<string>? conflicts = null, string? stateRaw = null)
    {
        conflicts ??= [];
        var metaCount = 0;
        if (_unityMode)
        {
            static bool IsMeta(string p) => p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
            metaCount += staged.RemoveAll(IsMeta);
            metaCount += modified.RemoveAll(IsMeta);
            metaCount += untracked.RemoveAll(IsMeta);
            metaCount += deleted.RemoveAll(IsMeta);
        }

        var clean = staged.Count == 0 && modified.Count == 0 && untracked.Count == 0 && deleted.Count == 0 && conflicts.Count == 0
            && (!_unityMode || metaCount == 0);
        var states = ExtractRepositoryStates(stateRaw).ToList();
        var sb = new StringBuilder();
        sb.Append(clean ? "ok status" : "status");
        sb.Append($" st={staged.Count} mod={modified.Count} untr={untracked.Count}");
        if (deleted.Count > 0)
            sb.Append($" del={deleted.Count}");
        if (conflicts.Count > 0)
            sb.Append($" conf={conflicts.Count}");
        if (_unityMode && metaCount > 0)
            sb.Append($" meta={metaCount}");
        if (!string.IsNullOrWhiteSpace(branch))
            sb.Append($" br={FormatBranch(branch, stateRaw)}");
        if (states.Count > 0)
            sb.Append($" state={string.Join("+", states)}");
        sb.AppendLine();

        foreach (var p in staged)
            sb.AppendLine($"  s:{p}");
        foreach (var p in modified)
            sb.AppendLine($"  m:{p}");
        foreach (var p in deleted)
            sb.AppendLine($"  D:{p}");
        foreach (var p in conflicts)
            sb.AppendLine($"  c:{p}");
        foreach (var p in untracked)
            sb.AppendLine($"  u:{p}");

        return sb.ToString();
    }

    private static bool LooksLikePorcelain(string[] lines) =>
        lines.Any(line => line.StartsWith("## "))
        || lines.All(line => line.Length >= 3 && PorcelainStatusRe().IsMatch(line));

    private string FormatPorcelain(string[] lines, string? stateRaw = null)
    {
        string? branch = null;
        var staged = new List<string>();
        var modified = new List<string>();
        var untracked = new List<string>();
        var deleted = new List<string>();
        var conflicts = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("## "))
            {
                branch = line["## ".Length..].Trim();
                continue;
            }

            var entry = GitPorcelain.ParseLine(line);
            if (entry is null)
                continue;

            if (entry.X == '?' && entry.Y == '?')
            {
                untracked.Add(entry.Path);
                continue;
            }

            if (IsConflict(entry.X, entry.Y))
            {
                conflicts.Add(entry.Path);
                continue;
            }

            if (entry.X == 'D' || entry.Y == 'D')
            {
                deleted.Add(entry.Path);
                continue;
            }

            if (entry.X != ' ' && entry.X != '?')
                staged.Add(entry.Path);
            if (entry.Y != ' ' && entry.Y != '?')
                modified.Add(entry.Path);
        }

        return FormatSummary(branch, staged, modified, untracked, deleted, conflicts, stateRaw);
    }

    /// <summary>Porcelain XY codes for an unmerged (conflicted) path: DD, AU, UD, UA, DU, AA, UU.</summary>
    private static bool IsConflict(char x, char y) =>
        x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D');

    private static IEnumerable<string> ExtractRepositoryStates(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            yield break;

        var lower = raw.ToLowerInvariant();
        if (lower.Contains("rebase in progress") || lower.Contains("rebasing"))
            yield return "rebase";
        if (lower.Contains("merge in progress") || lower.Contains("unmerged paths") || lower.Contains("fix conflicts and then commit"))
            yield return "merge";
        if (lower.Contains("cherry-pick"))
            yield return "cherry-pick";
        if (lower.Contains("revert currently in progress") || lower.Contains("reverting"))
            yield return "revert";
        if (lower.Contains("bisecting:"))
            yield return "bisect";
        if (lower.Contains("am in progress") || lower.Contains("apply mailbox"))
            yield return "am";
    }

    /// <summary>Renders the branch field, substituting the detached-HEAD short SHA (from
    /// <paramref name="stateRaw"/>, e.g. "HEAD detached at 2dc98d7") when git reports the
    /// branch-less porcelain header "HEAD (no branch)" — otherwise that identity is lost.</summary>
    private static string FormatBranch(string branch, string? stateRaw)
    {
        var branchOnly = ExtractBranchOnly(branch);
        if (branchOnly == "HEAD (no branch)" && ExtractDetachedShortSha(stateRaw) is { } sha)
            return $"HEAD@{sha}";
        return branchOnly.Replace(' ', '_');
    }

    private static string ExtractBranchOnly(string branch) =>
        branch.Split("...", 2, StringSplitOptions.None)[0].Trim();

    private static string? ExtractDetachedShortSha(string? stateRaw)
    {
        if (string.IsNullOrWhiteSpace(stateRaw))
            return null;
        var match = DetachedHeadRe().Match(stateRaw);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^(?:\?\?|!!|[ MARCUD][ MARCUD])\s")]
    private static partial Regex PorcelainStatusRe();

    [GeneratedRegex(@"HEAD detached (?:at|from) (\S+)")]
    private static partial Regex DetachedHeadRe();

    private enum Section { None, Staged, Modified, Untracked }
}
