using Tk;
using Tk.Filters;
using Tk.Tests.Snapshots;
using Xunit;

namespace Tk.Tests.Invariants;

/// <summary>
/// Runs each semantic invariant (see <see cref="SemanticChecks"/>) against every existing
/// golden-corpus fixture that matches its domain (see <see cref="InvariantCases"/>). These are
/// regression guards for the worst bug classes found by the tk 0.6.0 audit — they are expected
/// to pass today (the bugs they guard against are already fixed) and to keep passing as fixtures
/// are added or filters evolve.
/// </summary>
public class SemanticInvariantTests
{
    private static string ReadNormalized(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n").Replace("\r", "\n");

    private static (string CaseDir, FixtureMeta Meta, DetailLevel Level, string Input, string Output) Load(
        string area, string caseName, string levelName)
    {
        var caseDir = Path.Combine(SnapshotHarness.FixturesRoot, area, caseName);
        var meta = FixtureMeta.Load(Path.Combine(caseDir, "meta.json"));
        var level = Enum.Parse<DetailLevel>(levelName);
        var input = ReadNormalized(Path.Combine(caseDir, "input.txt"));
        var (output, _, _) = SnapshotHarness.RunFilter(caseDir, meta, level);
        return (caseDir, meta, level, input, output);
    }

    /// <summary>Binds to: fixtures/git/diff-cc-conflict (any future git-diff/git-show fixture
    /// whose input.txt contains "diff --cc" is checked automatically).</summary>
    [Theory]
    [MemberData(nameof(InvariantCases.ConflictDiffCases), MemberType = typeof(InvariantCases))]
    public void Conflict_diff_is_never_reported_empty(string area, string caseName, string level)
    {
        var (_, _, _, input, output) = Load(area, caseName, level);
        SemanticChecks.AssertConflictDiffNotEmpty(input, output);
    }

    /// <summary>Binds to: fixtures/log/not-a-log-file (and any log fixture with zero recognized
    /// entries across more than 10 non-empty lines, e.g. unparsable-garbage stays under the
    /// threshold today but would be caught the moment it grows past it).</summary>
    [Theory]
    [MemberData(nameof(InvariantCases.LogCases), MemberType = typeof(InvariantCases))]
    public void Unrecognized_log_file_exits_nonzero_and_does_not_say_ok(string area, string caseName, string level)
    {
        var caseDir = Path.Combine(SnapshotHarness.FixturesRoot, area, caseName);
        var meta = FixtureMeta.Load(Path.Combine(caseDir, "meta.json"));
        var input = ReadNormalized(Path.Combine(caseDir, "input.txt"));

        // The "log" filter reads from a file path and returns its own exit code (rather than
        // taking one in) — mirror SnapshotHarness's temp-file dance to get a real exit code
        // rather than the one RunFilter/SnapshotTests discards.
        var tmp = Path.Combine(Path.GetTempPath(), $"tk-invariant-{caseName}.log");
        File.WriteAllText(tmp, input);
        try
        {
            var detailLevel = Enum.Parse<DetailLevel>(level);
            var output = LogFileFilter.Apply(tmp, meta.Flags, detailLevel, out var exitCode);
            SemanticChecks.AssertLogZeroParseableExitsNonzero(input, output, exitCode);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>Binds to: fixtures/find/truncation-many-paths (any find fixture whose path count
    /// exceeds the detail level's cap is checked automatically).</summary>
    [Theory]
    [MemberData(nameof(InvariantCases.FindCases), MemberType = typeof(InvariantCases))]
    public void Truncated_find_output_discloses_hid_marker(string area, string caseName, string level)
    {
        var (_, _, detailLevel, input, output) = Load(area, caseName, level);
        SemanticChecks.AssertFindTruncationDisclosed(input, output, detailLevel == DetailLevel.More);
    }

    /// <summary>Binds to: fixtures/dotnet/test-failures-skip (any dotnet-test fixture with one or
    /// more "Failed &lt;name&gt; ... Error Message:" blocks is checked automatically).</summary>
    [Theory]
    [MemberData(nameof(InvariantCases.DotnetTestCases), MemberType = typeof(InvariantCases))]
    public void Failed_test_assertion_messages_survive_filtering(string area, string caseName, string level)
    {
        var (_, _, _, input, output) = Load(area, caseName, level);
        SemanticChecks.AssertFailedTestMessagesPresent(input, output);
    }

    /// <summary>Binds to: fixtures/git/status-midrebase (any git-status fixture whose input.txt
    /// and/or state.txt contains a rebase/merge/cherry-pick/etc. header is checked automatically).</summary>
    [Theory]
    [MemberData(nameof(InvariantCases.GitStatusCases), MemberType = typeof(InvariantCases))]
    public void Git_state_header_surfaces_state_field(string area, string caseName, string level)
    {
        var caseDir = Path.Combine(SnapshotHarness.FixturesRoot, area, caseName);
        var meta = FixtureMeta.Load(Path.Combine(caseDir, "meta.json"));
        var input = ReadNormalized(Path.Combine(caseDir, "input.txt"));
        var combined = meta.HasState
            ? input + "\n" + ReadNormalized(Path.Combine(caseDir, "state.txt"))
            : input;

        var (_, _, _, _, output) = Load(area, caseName, level);
        SemanticChecks.AssertGitStateSurfaced(combined, output);
    }
}
