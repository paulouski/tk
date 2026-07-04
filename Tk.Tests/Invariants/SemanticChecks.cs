using System.Text.RegularExpressions;
using Xunit;

namespace Tk.Tests.Invariants;

/// <summary>
/// Semantic invariant checks that guard the worst bug classes found by the tk 0.6.0 audit
/// (see MEMORY.md "tk audit findings"). Each check is a pure function over an (input, output)
/// pair (plus whatever minimal extra context it needs, e.g. exit code) — it is deliberately NOT
/// hardcoded to any one fixture name, so any current or future fixture routed to the matching
/// filter domain (see <see cref="InvariantCases"/>) is checked automatically.
///
/// These are independent oracles: each check re-derives its expectation from the raw input via
/// its own lightweight parsing, rather than calling back into the production filter code, so a
/// regression in the filter can't silently satisfy its own check.
/// </summary>
public static class SemanticChecks
{
    /// <summary>
    /// Guards against a combined-diff ("diff --cc", produced for unresolved merge conflicts)
    /// being reported as an empty diff. Before this was fixed, conflict markers in "diff --cc"
    /// format used a different +/- prefix convention than a normal unified diff, and a filter
    /// that only recognized the normal convention would count zero added/removed lines and zero
    /// files — silently hiding the conflict from the agent.
    /// </summary>
    public static void AssertConflictDiffNotEmpty(string input, string output)
    {
        if (!input.Contains("diff --cc "))
            return; // precondition not met — this fixture isn't a combined/conflict diff.

        var match = ConflictHeaderRe.Match(output);
        if (!match.Success)
            return; // a header in some other shape (e.g. "ok diff f=0" with no counts) — not the
                    // f=0/+0/-0 shape this check guards against.

        var files = int.Parse(match.Groups["f"].Value);
        var added = int.Parse(match.Groups["add"].Value);
        var removed = int.Parse(match.Groups["del"].Value);

        Assert.False(files == 0 && added == 0 && removed == 0,
            "Conflict-diff invariant violated: input contains 'diff --cc' (a merge-conflict " +
            $"combined diff) but output reports f=0 +0 -0 (looks empty):\n{output}");
    }

    /// <summary>
    /// Guards against a log file with no recognizable ASP.NET log entries being silently
    /// reported as "ok" — e.g. a source file or config dump accidentally passed to `tk log`.
    /// Recognizes headers independently of <c>LogFileFilter</c>'s own regex (a separate oracle)
    /// so a regression in the filter's own recognition can't trivially satisfy this check.
    /// </summary>
    public static void AssertLogZeroParseableExitsNonzero(string input, string output, int exitCode)
    {
        var lines = input.Replace("\r\n", "\n").Split('\n');
        var nonEmptyCount = lines.Count(l => !string.IsNullOrWhiteSpace(l));
        var recognizedCount = lines.Count(l => LogHeaderRe.IsMatch(l));

        const int nonLogLineThreshold = 10; // mirrors LogFileFilter.NonLogLineThreshold
        if (recognizedCount != 0 || nonEmptyCount <= nonLogLineThreshold)
            return; // precondition not met — either it parsed something, or it's short enough
                     // that silence is fine either way.

        Assert.True(exitCode != 0,
            $"Log invariant violated: 0 recognized entries across {nonEmptyCount} non-empty " +
            $"lines (> {nonLogLineThreshold}) but exit code was 0:\n{output}");
        Assert.False(output.TrimStart().StartsWith("ok", StringComparison.Ordinal),
            $"Log invariant violated: 0 recognized entries across {nonEmptyCount} non-empty " +
            $"lines (> {nonLogLineThreshold}) but output claims success ('ok...'):\n{output}");
    }

    /// <summary>
    /// Guards against `find` silently truncating its path list without disclosing it. Recomputes
    /// the truncation decision independently from the raw input (same caps FindFilter uses) so a
    /// regression that drops the footer without changing the cap logic still gets caught.
    /// </summary>
    public static void AssertFindTruncationDisclosed(string input, string output, bool moreLevel)
    {
        var lines = input.Replace("\r\n", "\n").Split('\n');
        var pathCount = lines.Count(l =>
            !string.IsNullOrWhiteSpace(l) &&
            !l.StartsWith("find:") &&
            !l.Contains("Permission denied"));

        var maxPaths = moreLevel ? 20 : 5; // mirrors FindFilter's _maxPaths
        if (pathCount <= maxPaths)
            return; // precondition not met — nothing was truncated.

        Assert.Contains("hid=", output);
    }

    /// <summary>
    /// Guards against a failed test's assertion/exception message being dropped from the
    /// compacted output — the one line an agent actually needs to diagnose the failure.
    /// Parses "Failed &lt;name&gt; ... Error Message: &lt;message&gt;" blocks straight out of the
    /// raw dotnet-test input (independent of DotnetTestFilter's own parsing) and asserts the
    /// first message line for every failed test survives into the filtered output.
    /// </summary>
    public static void AssertFailedTestMessagesPresent(string input, string output)
    {
        var lines = input.Replace("\r\n", "\n").Split('\n');
        string? currentMessage = null;
        var inMessage = false;
        var checkedAny = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (FailedTestRe.IsMatch(line))
            {
                inMessage = false;
                currentMessage = null;
                continue;
            }

            if (ErrorMessageHeaderRe.IsMatch(line))
            {
                inMessage = true;
                continue;
            }

            if (StackTraceHeaderRe.IsMatch(line) || string.IsNullOrWhiteSpace(line))
            {
                inMessage = false;
                continue;
            }

            if (inMessage && currentMessage is null)
            {
                currentMessage = line.Trim();
                if (currentMessage.Length > 0)
                {
                    checkedAny = true;
                    Assert.Contains(currentMessage, output);
                }
            }
        }

        _ = checkedAny; // presence of >=1 checked message is asserted per-message above; a
                         // fixture with no failed-test blocks simply has nothing to check here.
    }

    /// <summary>
    /// Guards against an in-progress repo state (rebase/merge/cherry-pick/etc.) being silently
    /// dropped from `tk git status` output — the agent needs to know it's mid-operation.
    /// Scans the combined input (porcelain status + the plain-text state probe, when present)
    /// for the same header phrases GitStatusFilter.ExtractRepositoryStates recognizes, as an
    /// independent oracle, and requires "state=" to appear in the output when any are found.
    /// </summary>
    public static void AssertGitStateSurfaced(string combinedInput, string output)
    {
        var lower = combinedInput.ToLowerInvariant();
        var found = StateHeaderPhrases.Any(lower.Contains);
        if (!found)
            return; // precondition not met — no state header in the input at all.

        Assert.Contains("state=", output);
    }

    private static readonly string[] StateHeaderPhrases =
    [
        "rebase in progress", "rebasing",
        "merge in progress", "unmerged paths", "fix conflicts and then commit",
        "cherry-pick",
        "revert currently in progress", "reverting",
        "bisecting:",
        "am in progress", "apply mailbox",
    ];

    private static readonly Regex ConflictHeaderRe =
        new(@"diff f=(?<f>\d+) \+(?<add>\d+) -(?<del>\d+)", RegexOptions.Compiled);

    private static readonly Regex LogHeaderRe =
        new(@"^(?:dbug|info|warn|fail|crit|error)\s*:\s*[^\[]+?(?:\[\d+\])?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FailedTestRe =
        new(@"^\s+Failed\s+(.+?)(?:\s+\[\S+\])?\s*$", RegexOptions.Compiled);

    private static readonly Regex ErrorMessageHeaderRe =
        new(@"^\s*Error Message:\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StackTraceHeaderRe =
        new(@"^\s*Stack Trace:\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
