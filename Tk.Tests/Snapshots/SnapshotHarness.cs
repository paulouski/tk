using System.Runtime.CompilerServices;
using Tk;
using Tk.Common;
using Tk.Filters;
using Xunit;

namespace Tk.Tests.Snapshots;

/// <summary>
/// Discovers fixture cases under Tk.Tests/fixtures/&lt;area&gt;/&lt;case&gt;/, runs the filter
/// declared in each case's meta.json in-process (no process spawning), normalizes the output,
/// and either asserts it against the checked-in expected.&lt;level&gt;.txt snapshot or — when
/// TK_UPDATE_SNAPSHOTS=1 — (re)writes that snapshot.
///
/// See fixtures/README.md for the fixture format and update-mode instructions.
/// </summary>
public static class SnapshotHarness
{
    public static bool UpdateMode =>
        Environment.GetEnvironmentVariable("TK_UPDATE_SNAPSHOTS") == "1";

    public static string FixturesRoot { get; } = ResolveFixturesRoot();

    private static string ResolveFixturesRoot([CallerFilePath] string thisFile = "")
    {
        // Tk.Tests/Snapshots/SnapshotHarness.cs -> Tk.Tests/fixtures
        var snapshotsDir = Path.GetDirectoryName(thisFile)!;
        var testsDir = Path.GetDirectoryName(snapshotsDir)!;
        return Path.Combine(testsDir, "fixtures");
    }

    /// <summary>One row per (area, case, detailLevel) — the xunit Theory data source.</summary>
    public static IEnumerable<object[]> DiscoverCases()
    {
        if (!Directory.Exists(FixturesRoot))
            yield break;

        foreach (var areaDir in Directory.GetDirectories(FixturesRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var area = Path.GetFileName(areaDir);
            foreach (var caseDir in Directory.GetDirectories(areaDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var metaPath = Path.Combine(caseDir, "meta.json");
                if (!File.Exists(metaPath))
                    continue;

                var meta = FixtureMeta.Load(metaPath);
                foreach (var level in meta.DetailLevels)
                    yield return [area, Path.GetFileName(caseDir), level];
            }
        }
    }

    public static void RunAndAssert(string area, string caseName, string detailLevelName)
    {
        var caseDir = Path.Combine(FixturesRoot, area, caseName);
        var meta = FixtureMeta.Load(Path.Combine(caseDir, "meta.json"));
        var level = Enum.Parse<DetailLevel>(detailLevelName);

        var (actualRaw, ledger, inputUnits) = RunFilter(caseDir, meta, level);

        // Conservation invariant (docs/output-contract.md): every input unit the filter looked
        // at must land in exactly one of Kept/Summarized/Hidden/Unparsed.
        ledger.AssertConserves(inputUnits);

        var isGitArea = area == "git";
        var actual = SnapshotNormalizer.Normalize(actualRaw, isGitArea);

        var expectedPath = Path.Combine(caseDir, $"expected.{detailLevelName.ToLowerInvariant()}.txt");

        if (UpdateMode)
        {
            File.WriteAllText(expectedPath, actual);
            return;
        }

        if (!File.Exists(expectedPath))
        {
            Assert.Fail(
                $"Missing snapshot: {expectedPath}\n" +
                $"Run with TK_UPDATE_SNAPSHOTS=1 to generate it after reviewing this output:\n---\n{actual}");
        }

        var expected = SnapshotNormalizer.Normalize(ReadAllTextNormalizedNewlines(expectedPath), isGitArea);

        if (expected != actual)
        {
            Assert.Fail(
                $"Snapshot mismatch for {area}/{caseName} [{detailLevelName}]\n" +
                $"(rerun with TK_UPDATE_SNAPSHOTS=1 to accept the new output, after reviewing the diff)\n\n" +
                DiffFormatter.Unified(expected, actual));
        }
    }

    private static string ReadAllTextNormalizedNewlines(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>Runs the fixture's declared filter in-process and returns its output, the
    /// <see cref="UnitLedger"/> it classified every input unit into, and the true input-unit
    /// count (physical lines of input.txt) the ledger must conserve against.</summary>
    private static (string Output, UnitLedger Ledger, int InputUnits) RunFilter(string caseDir, FixtureMeta meta, DetailLevel level)
    {
        var rawPath = Path.Combine(caseDir, "input.txt");
        var raw = ReadAllTextNormalizedNewlines(rawPath);
        // LogFileFilter deliberately keeps its internal `lines` array (and footer "total")
        // untrimmed — i.e. it counts a trailing split artifact from a final '\n' as one more
        // (blank, Hidden) physical line, matching its pre-existing hid=X/Y byte-for-byte. Every
        // other filter trims that artifact before classifying, so its ledger conserves against
        // the trimmed HiddenLinesFooter.CountLines. Match whichever this fixture's filter uses.
        var inputUnits = meta.Filter == "log" ? raw.Split('\n').Length : HiddenLinesFooter.CountLines(raw);
        var ledger = new UnitLedger();

        switch (meta.Filter)
        {
            case "dotnet-build":
                return (WithFooter(raw, new DotnetBuildFilter().Apply(raw, meta.ExitCode, ledger), level, ledger), ledger, inputUnits);

            case "dotnet-test":
                return (WithFooter(raw, new DotnetTestFilter(level).Apply(raw, meta.ExitCode, ledger), level, ledger), ledger, inputUnits);

            case "dotnet-restore":
                return (WithFooter(raw, new DotnetRestoreFilter().Apply(raw, meta.ExitCode, ledger), level, ledger), ledger, inputUnits);

            case "git-status":
            {
                string? state = meta.HasState
                    ? ReadAllTextNormalizedNewlines(Path.Combine(caseDir, "state.txt"))
                    : null;
                var filtered = new GitStatusFilter(level, meta.UnityMode).Apply(raw, meta.ExitCode, state, ledger);
                return (WithFooter(raw, filtered, level, ledger), ledger, inputUnits);
            }

            case "git-diff":
                return (WithFooter(raw, new GitDiffFilter(level, isShow: false).Apply(raw, meta.ExitCode, ledger), level, ledger), ledger, inputUnits);

            case "git-show":
                return (WithFooter(raw, new GitDiffFilter(level, isShow: true).Apply(raw, meta.ExitCode, ledger), level, ledger), ledger, inputUnits);

            case "git-log":
                return (WithFooter(raw, new GitLogFilter().Apply(raw, meta.ExitCode, ledger), level, ledger), ledger, inputUnits);

            case "git-compact":
                return (WithFooter(raw, new GitCompactFilter().Apply(raw, meta.ExitCode, ledger), level, ledger), ledger, inputUnits);

            case "grep":
            case "rg":
                return (WithFooter(raw, new GrepFilter(meta.Command, level, meta.Pattern).Apply(raw, meta.ExitCode, ledger), level, ledger), ledger, inputUnits);

            case "find":
                return (WithFooter(raw, new FindFilter(level).Apply(raw, meta.ExitCode, ledger), level, ledger), ledger, inputUnits);

            case "log":
            {
                // LogFileFilter reads from a file path rather than a raw string — materialize
                // the fixture's raw content to a throwaway temp file for this in-process call.
                // LogFileFilter already appends its own footer internally, so it is NOT wrapped
                // with WithFooter here (unlike every other filter above).
                // The temp file name is stable (derived from the case name, not a fresh Guid
                // per run) because LogFileFilter echoes it back in the "file=" field of its
                // output — a random name would make the snapshot non-deterministic.
                var tmp = Path.Combine(Path.GetTempPath(), $"tk-snapshot-{Path.GetFileName(caseDir)}.log");
                File.WriteAllText(tmp, raw);
                try
                {
                    return (LogFileFilter.Apply(tmp, meta.Flags, level, out _, ledger), ledger, inputUnits);
                }
                finally
                {
                    File.Delete(tmp);
                }
            }

            default:
                throw new InvalidOperationException($"Unknown fixture filter '{meta.Filter}' in {caseDir}");
        }
    }

    /// <summary>Mirrors the shared footer renderer that Program.cs / GitCommand.cs apply around
    /// every filter's output in production (all filters except "log", which appends its own
    /// footer internally — see the "log" case above). No raw= reference here: saving a raw copy
    /// is a live-command concern (RawOutputStore), not exercised by this in-process harness.</summary>
    private static string WithFooter(string raw, string filtered, DetailLevel level, UnitLedger ledger)
    {
        var footer = OutputFooter.Format(
            HiddenLinesFooter.CountLines(raw),
            HiddenLinesFooter.CountLines(filtered),
            ledger.UnparsedCount,
            level);
        if (footer is null)
            return filtered;
        return filtered.EndsWith('\n') ? $"{filtered}{footer}\n" : $"{filtered}\n{footer}\n";
    }
}
