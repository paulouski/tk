using Tk;
using Tk.Commands;
using Tk.Common;
using Tk.Filters;
using Xunit;

namespace Tk.Tests.Filters;

/// <summary>
/// Defense in depth for the output contract (see docs/output-contract.md): for every compacting
/// <see cref="IOutputFilter"/> (plus <see cref="LogFileFilter"/>, which appends its own footer
/// internally), feed synthetic input the filter demonstrably reduces/hides, then assert a
/// truthful footer is actually appended. This does not catch the fetch-level `git log` cap (see
/// <c>GitCommandTests.Log_without_limit_reports_truncated_history_beyond_cap</c> for that) — it
/// only guards that no filter can silently drop content it received without saying so.
/// </summary>
[Collection("RawOutputStoreEnv")]
public class OutputContractFooterInvariantTests
{
    private static string AppendedFooter(string raw, string filtered, UnitLedger ledger, int exitCode, string[] commandArgs) =>
        OutputPipeline.AppendFooter(raw, filtered, DetailLevel.Default, ledger, exitCode, commandArgs);

    [Fact]
    public void DotnetBuildFilter_hides_boilerplate_lines_and_gets_a_footer()
    {
        var raw = """
            Microsoft (R) Build Engine

              Determining projects to restore...
              Tk -> /repo/tk/bin/Debug/net8.0/tk.dll

            Build succeeded.
                Time Elapsed 00:00:01.23
            """;
        var ledger = new UnitLedger();
        var filtered = new DotnetBuildFilter().Apply(raw, 0, ledger);

        var appended = AppendedFooter(raw, filtered, ledger, 0, ["dotnet", "build"]);
        Assert.Contains("hid=", appended);
    }

    [Fact]
    public void DotnetTestFilter_hides_the_time_elapsed_line_and_gets_a_footer()
    {
        var raw = """
            Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 1 s
                Time Elapsed 00:00:01.23
            """;
        var ledger = new UnitLedger();
        var filtered = new DotnetTestFilter().Apply(raw, 0, ledger);

        var appended = AppendedFooter(raw, filtered, ledger, 0, ["dotnet", "test"]);
        Assert.Contains("hid=", appended);
    }

    [Fact]
    public void DotnetRestoreFilter_hides_boilerplate_lines_and_gets_a_footer()
    {
        var raw = """
              Determining projects to restore...
              Restored /repo/A.csproj (in 1 sec).
              Restored /repo/B.csproj (in 2 sec).
                Time Elapsed 00:00:03.00
            """;
        var ledger = new UnitLedger();
        var filtered = new DotnetRestoreFilter().Apply(raw, 0, ledger);

        var appended = AppendedFooter(raw, filtered, ledger, 0, ["dotnet", "restore"]);
        Assert.Contains("hid=", appended);
    }

    [Fact]
    public void GitStatusFilter_unity_mode_hides_meta_paths_and_gets_a_footer()
    {
        var raw = """
            ## main
            M  Assets/Foo.cs
            M  Assets/Foo.cs.meta
             M Assets/Bar.shader
             M Assets/Bar.shader.meta
            ?? Assets/New.cs
            ?? Assets/New.cs.meta
            """;
        var ledger = new UnitLedger();
        var filtered = new GitStatusFilter(DetailLevel.Default, unityMode: true).Apply(raw, 0, ledger);

        var appended = AppendedFooter(raw, filtered, ledger, 0, ["git", "status"]);
        Assert.Contains("hid=", appended);
    }

    [Fact]
    public void GitDiffFilter_collapses_many_hunks_and_gets_a_footer()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 5; i++)
        {
            sb.AppendLine($"diff --git a/f{i}.cs b/f{i}.cs");
            sb.AppendLine("index aaa..bbb 100644");
            sb.AppendLine($"--- a/f{i}.cs");
            sb.AppendLine($"+++ b/f{i}.cs");
            sb.AppendLine("@@ -1 +1 @@");
            sb.AppendLine("-old");
            sb.AppendLine("+new");
        }
        var raw = sb.ToString();
        var ledger = new UnitLedger();
        var filtered = new GitDiffFilter(DetailLevel.Default).Apply(raw, 0, ledger);

        var appended = AppendedFooter(raw, filtered, ledger, 0, ["git", "diff"]);
        Assert.Contains("hid=", appended);
    }

    [Fact]
    public void GitLogFilter_truncates_beyond_50_commits_and_gets_a_footer()
    {
        var lines = Enumerable.Range(1, 60).Select(i => $"hash{i:D4} message {i}").ToArray();
        var raw = string.Join('\n', lines) + "\n";
        var ledger = new UnitLedger();
        var filtered = new GitLogFilter().Apply(raw, 0, ledger);

        var appended = AppendedFooter(raw, filtered, ledger, 0, ["git", "log"]);
        Assert.Contains("hid=", appended);
    }

    [Fact]
    public void GitCompactFilter_keeps_head_and_tail_and_gets_a_footer()
    {
        var raw = "a\nb\nc\nd\ne\nf\ng\nh\n";
        var ledger = new UnitLedger();
        var filtered = new GitCompactFilter().Apply(raw, 0, ledger);

        var appended = AppendedFooter(raw, filtered, ledger, 0, ["git", "checkout", "main"]);
        Assert.Contains("hid=", appended);
    }

    [Fact]
    public void FindFilter_truncates_to_5_paths_and_gets_a_footer()
    {
        // 20 raw paths so the capped-to-5 output (plus header/top-groups/"+N more" lines) is
        // still strictly shorter than the input — a smaller N (e.g. 9) nets out to the same
        // line count once the added summary lines are counted, which would hide nothing.
        var raw = string.Join('\n', Enumerable.Range(0, 20).Select(i => $"/r/dir/f{i}.cs"));
        var ledger = new UnitLedger();
        var filtered = new FindFilter(DetailLevel.Default).Apply(raw, 0, ledger);

        var appended = AppendedFooter(raw, filtered, ledger, 0, ["find", "."]);
        Assert.Contains("hid=", appended);
    }

    [Fact]
    public void GrepFilter_collapses_many_matches_to_top_files_and_gets_a_footer()
    {
        var raw = string.Join('\n',
            Enumerable.Range(0, 5).SelectMany(f =>
                Enumerable.Range(0, f + 1).Select(_ => $"src/f{f}.cs:1:hit{f}")));
        var ledger = new UnitLedger();
        var filtered = new GrepFilter("grep", DetailLevel.Default).Apply(raw, 0, ledger);

        var appended = AppendedFooter(raw, filtered, ledger, 0, ["grep", "-rn", "hit"]);
        Assert.Contains("hid=", appended);
    }

    [Fact]
    public void LogFileFilter_hides_info_lines_below_warn_and_gets_its_own_footer()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "tk-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var path = Path.Combine(tempDir, "service.log");
            File.WriteAllText(path, """
                info: Some.Source[0]
                      Application started
                warn: Some.Source[0]
                      Disk almost full
                fail: Some.Source[0]
                      Boom
                """);

            // LogFileFilter appends its own hid=/N footer internally (see docs/output-contract.md)
            // rather than going through OutputPipeline.AppendFooter — assert directly on its output.
            var actual = LogFileFilter.Apply(path, [], DetailLevel.Default, out _, new UnitLedger());

            Assert.Contains("hid=", actual);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }
}
