using Tk.Tests.Snapshots;

namespace Tk.Tests.Invariants;

/// <summary>
/// Filters the existing golden-corpus fixture discovery (<see cref="SnapshotHarness.DiscoverCases"/>)
/// down to the fixtures each semantic-invariant check applies to, keyed by the fixture's declared
/// <c>meta.json</c> filter — not by fixture name. Any fixture added later under a matching filter
/// automatically flows into the relevant Theory below; nothing needs to be registered by hand.
/// </summary>
public static class InvariantCases
{
    private sealed record Row(string Area, string Case, string Level, string CaseDir, FixtureMeta Meta);

    private static IEnumerable<Row> All()
    {
        foreach (var row in SnapshotHarness.DiscoverCases())
        {
            var area = (string)row[0];
            var caseName = (string)row[1];
            var level = (string)row[2];
            var caseDir = Path.Combine(SnapshotHarness.FixturesRoot, area, caseName);
            var meta = FixtureMeta.Load(Path.Combine(caseDir, "meta.json"));
            yield return new Row(area, caseName, level, caseDir, meta);
        }
    }

    public static IEnumerable<object[]> ConflictDiffCases() =>
        All().Where(r => r.Meta.Filter is "git-diff" or "git-show")
             .Select(r => new object[] { r.Area, r.Case, r.Level });

    public static IEnumerable<object[]> LogCases() =>
        All().Where(r => r.Meta.Filter == "log")
             .Select(r => new object[] { r.Area, r.Case, r.Level });

    public static IEnumerable<object[]> FindCases() =>
        All().Where(r => r.Meta.Filter == "find")
             .Select(r => new object[] { r.Area, r.Case, r.Level });

    public static IEnumerable<object[]> DotnetTestCases() =>
        All().Where(r => r.Meta.Filter == "dotnet-test")
             .Select(r => new object[] { r.Area, r.Case, r.Level });

    public static IEnumerable<object[]> GitStatusCases() =>
        All().Where(r => r.Meta.Filter == "git-status" && r.Meta.ExitCode == 0)
             .Select(r => new object[] { r.Area, r.Case, r.Level });
}
