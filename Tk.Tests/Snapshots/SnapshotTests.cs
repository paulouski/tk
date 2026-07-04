using Xunit;

namespace Tk.Tests.Snapshots;

/// <summary>
/// Golden-corpus regression oracle for tk's output filters (Phase D0). Runs every fixture
/// under Tk.Tests/fixtures/ through its declared filter, in-process, and compares the
/// normalized output against a checked-in snapshot. See fixtures/README.md.
/// </summary>
public class SnapshotTests
{
    public static IEnumerable<object[]> Cases() => SnapshotHarness.DiscoverCases();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Filter_output_matches_snapshot(string area, string caseName, string detailLevel)
    {
        SnapshotHarness.RunAndAssert(area, caseName, detailLevel);
    }
}
