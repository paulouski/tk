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

    public string Apply(string raw, int exitCode) => Apply(raw, exitCode, stateRaw: null, new UnitLedger());

    public string Apply(string raw, int exitCode, UnitLedger ledger) =>
        Apply(raw, exitCode, stateRaw: null, ledger);

    public string Apply(string raw, int exitCode, string? stateRaw) =>
        Apply(raw, exitCode, stateRaw, new UnitLedger());

    public string Apply(string raw, int exitCode, string? stateRaw, UnitLedger ledger)
    {
        var totalLines = HiddenLinesFooter.CountLines(raw);
        if (exitCode != 0)
        {
            ledger.Keep(totalLines);
            return raw;
        }

        var rawLines = raw.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var blankCount = 0;
        var lines = new List<string>();
        foreach (var l in rawLines)
        {
            if (string.IsNullOrWhiteSpace(l))
                blankCount++;
            else
                lines.Add(l);
        }
        if (rawLines.Length > 0 && rawLines[^1] == string.Empty)
            blankCount--;

        if (lines.Count == 0)
        {
            ledger.Hide(blankCount);
            return "ok status st=0 mod=0 untr=0\n";
        }

        if (LooksLikePorcelain(lines.ToArray()))
            return FormatPorcelain(lines.ToArray(), stateRaw, ledger, blankCount);

        var staged = new List<string>();
        var modified = new List<string>();
        var untracked = new List<string>();
        var deleted = new List<string>();
        string? branch = null;
        var recognizedBoilerplate = 0;
        var unrecognized = 0;

        var section = Section.None;

        foreach (var line in lines)
        {
            if (line.StartsWith("On branch "))
            {
                branch = line["On branch ".Length..].Trim();
                recognizedBoilerplate++;
                continue;
            }

            if (line.StartsWith("Changes to be committed:"))
            {
                section = Section.Staged;
                recognizedBoilerplate++;
                continue;
            }
            if (line.StartsWith("Changes not staged"))
            {
                section = Section.Modified;
                recognizedBoilerplate++;
                continue;
            }
            if (line.StartsWith("Untracked files:"))
            {
                section = Section.Untracked;
                recognizedBoilerplate++;
                continue;
            }

            if (line.StartsWith("Your branch ") || line.StartsWith("  (use ") ||
                line.StartsWith("no changes added"))
            {
                recognizedBoilerplate++;
                continue;
            }

            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                // Already excluded from `lines` (blank), unreachable — kept for parity with
                // the original control flow.
                recognizedBoilerplate++;
                continue;
            }

            if (!line.StartsWith("\t"))
            {
                // Not a recognized boilerplate line and not tab-indented content — alien input.
                unrecognized++;
                continue;
            }

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
                default:
                    // Tab-indented content with no known section context.
                    unrecognized++;
                    break;
            }
        }

        ledger.Hide(blankCount + recognizedBoilerplate);
        ledger.Unparsed(unrecognized);

        // FormatSummary mutates these same list instances in place (unity-mode meta-stripping);
        // measure before/after so the delta becomes Summarized (represented by meta=N) and the
        // remainder stays Kept (shown verbatim), whatever FormatSummary does internally.
        var preStripTotal = staged.Count + modified.Count + untracked.Count + deleted.Count;
        var result = FormatSummary(branch, staged, modified, untracked, deleted, stateRaw: stateRaw);
        var postStripTotal = staged.Count + modified.Count + untracked.Count + deleted.Count;
        ledger.Summarize(preStripTotal - postStripTotal);
        ledger.Keep(postStripTotal);

        return result;
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

    private string FormatPorcelain(string[] lines, string? stateRaw, UnitLedger ledger, int blankCount)
    {
        string? branch = null;
        var staged = new List<string>();
        var modified = new List<string>();
        var untracked = new List<string>();
        var deleted = new List<string>();
        var conflicts = new List<string>();
        var headerCount = 0;
        var unparsedCount = 0;
        var provisionallyKept = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith("## "))
            {
                branch = line["## ".Length..].Trim();
                headerCount++;
                continue;
            }

            var entry = GitPorcelain.ParseLine(line);
            if (entry is null)
            {
                unparsedCount++;
                continue;
            }

            // One recognized porcelain line, whatever it maps to below.
            provisionallyKept++;

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

        ledger.Hide(blankCount + headerCount);
        ledger.Unparsed(unparsedCount);

        // FormatSummary mutates these list instances in place (unity-mode meta-stripping) —
        // measure the total number of list slots before/after so the delta becomes Summarized
        // (represented by meta=N) and the remainder stays Kept. A dual-status porcelain line
        // (e.g. "MM") occupies two slots (staged + modified) from one input line; the algebra
        // below still conserves against `provisionallyKept` regardless, since Keep+Summarize is
        // just that one bucket split further.
        var preStripTotal = staged.Count + modified.Count + untracked.Count + deleted.Count + conflicts.Count;
        var result = FormatSummary(branch, staged, modified, untracked, deleted, conflicts, stateRaw);
        var postStripTotal = staged.Count + modified.Count + untracked.Count + deleted.Count + conflicts.Count;
        var slotsRemoved = preStripTotal - postStripTotal;
        // A dual-status meta line removes 2 slots but was only 1 provisionally-kept unit; cap
        // the delta so Summarize never exceeds what was actually classified.
        var summarized = Math.Min(slotsRemoved, provisionallyKept);
        ledger.Summarize(summarized);
        ledger.Keep(provisionallyKept - summarized);

        return result;
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
