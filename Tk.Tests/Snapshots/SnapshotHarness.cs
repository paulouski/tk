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

        var actualRaw = RunFilter(caseDir, meta, level);
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

    private static string RunFilter(string caseDir, FixtureMeta meta, DetailLevel level)
    {
        var rawPath = Path.Combine(caseDir, "input.txt");
        var raw = ReadAllTextNormalizedNewlines(rawPath);

        switch (meta.Filter)
        {
            case "dotnet-build":
                return WithHiddenLinesFooter(raw, new DotnetBuildFilter().Apply(raw, meta.ExitCode), level);

            case "dotnet-test":
                return WithHiddenLinesFooter(raw, new DotnetTestFilter(level).Apply(raw, meta.ExitCode), level);

            case "dotnet-restore":
                return WithHiddenLinesFooter(raw, new DotnetRestoreFilter().Apply(raw, meta.ExitCode), level);

            case "git-status":
            {
                string? state = meta.HasState
                    ? ReadAllTextNormalizedNewlines(Path.Combine(caseDir, "state.txt"))
                    : null;
                var filtered = new GitStatusFilter(level, meta.UnityMode).Apply(raw, meta.ExitCode, state);
                return WithHiddenLinesFooter(raw, filtered, level);
            }

            case "git-diff":
                return WithHiddenLinesFooter(raw, new GitDiffFilter(level, isShow: false).Apply(raw, meta.ExitCode), level);

            case "git-show":
                return WithHiddenLinesFooter(raw, new GitDiffFilter(level, isShow: true).Apply(raw, meta.ExitCode), level);

            case "git-log":
                return WithHiddenLinesFooter(raw, new GitLogFilter().Apply(raw, meta.ExitCode), level);

            case "git-compact":
                return WithHiddenLinesFooter(raw, new GitCompactFilter().Apply(raw, meta.ExitCode), level);

            case "grep":
            case "rg":
                return WithHiddenLinesFooter(raw, new GrepFilter(meta.Command, level, meta.Pattern).Apply(raw, meta.ExitCode), level);

            case "find":
                return WithHiddenLinesFooter(raw, new FindFilter(level).Apply(raw, meta.ExitCode), level);

            case "log":
            {
                // LogFileFilter reads from a file path rather than a raw string — materialize
                // the fixture's raw content to a throwaway temp file for this in-process call.
                // LogFileFilter already appends its own hidden-lines footer internally, so it is
                // NOT wrapped with WithHiddenLinesFooter here (unlike every other filter above).
                // The temp file name is stable (derived from the case name, not a fresh Guid
                // per run) because LogFileFilter echoes it back in the "file=" field of its
                // output — a random name would make the snapshot non-deterministic.
                var tmp = Path.Combine(Path.GetTempPath(), $"tk-snapshot-{Path.GetFileName(caseDir)}.log");
                File.WriteAllText(tmp, raw);
                try
                {
                    return LogFileFilter.Apply(tmp, meta.Flags, level);
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

    /// <summary>Mirrors the hidden-lines footer wrapping that Program.cs / GitCommand.cs apply
    /// around every filter's output in production (all filters except "log", which appends its
    /// own footer internally — see the "log" case above).</summary>
    private static string WithHiddenLinesFooter(string raw, string filtered, DetailLevel level)
    {
        var footer = HiddenLinesFooter.Format(
            HiddenLinesFooter.CountLines(raw),
            HiddenLinesFooter.CountLines(filtered),
            level);
        if (footer is null)
            return filtered;
        return filtered.EndsWith('\n') ? $"{filtered}{footer}\n" : $"{filtered}\n{footer}\n";
    }
}
