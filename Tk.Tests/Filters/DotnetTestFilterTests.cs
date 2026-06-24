using Tk.Filters;
using Xunit;

namespace Tk.Tests.Filters;

public class DotnetTestFilterTests
{
    [Fact]
    public void Passing_summary_reports_ok_with_counts()
    {
        var raw = """
            Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 1 s
                Time Elapsed 00:00:01.23
            """;
        var actual = new DotnetTestFilter().Apply(raw, 0);
        Assert.StartsWith("ok test pass=10 p=1 t=00:00:01.23\n", actual);
        Assert.DoesNotContain("Failed:", actual);
    }

    [Fact]
    public void Skipped_count_included()
    {
        var raw = "Passed!  - Failed: 0, Passed: 8, Skipped: 2, Total: 10, Duration: 1 s\n";
        var actual = new DotnetTestFilter().Apply(raw, 0);
        Assert.Contains("pass=8", actual);
        Assert.Contains("skip=2", actual);
    }

    [Fact]
    public void Failing_summary_lists_failed_tests_with_details()
    {
        var raw = """
            Failed!  - Failed:     1, Passed:     9, Skipped:     0, Total:    10, Duration: 1 s
              Failed Tk.Tests.Foo.Bar [12 ms]
                Expected: 1
                Actual:   2
                Stack at line 7

                Time Elapsed 00:00:00.50
            """;
        var actual = new DotnetTestFilter().Apply(raw, 1);
        Assert.StartsWith("FAIL test pass=9 fail=1", actual);
        Assert.Contains("Failed:", actual);
        Assert.Contains("Tk.Tests.Foo.Bar", actual);
        Assert.Contains("Expected: 1", actual);
        Assert.Contains("Actual:   2", actual);
    }

    [Fact]
    public void Multiple_test_projects_aggregated()
    {
        var raw = """
            Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: 1 s
            Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 1 s
            """;
        var actual = new DotnetTestFilter().Apply(raw, 0);
        Assert.Contains("pass=8", actual);
        Assert.Contains("p=2", actual);
    }

    [Fact]
    public void Non_zero_exit_with_no_summary_appends_raw_tail()
    {
        var raw = """
            Build error
            something exploded
            """;
        var actual = new DotnetTestFilter().Apply(raw, 1);
        Assert.StartsWith("FAIL test e=1\n", actual);
        Assert.Contains("--- raw tail ---", actual);
    }

    [Fact]
    public void All_failed_tests_shown_no_truncation()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Failed!  - Failed: 20, Passed: 0, Skipped: 0, Total: 20, Duration: 1 s");
        for (var i = 0; i < 20; i++)
            sb.AppendLine($"  Failed Test{i:D2} [1 ms]");
        var actual = new DotnetTestFilter().Apply(sb.ToString(), 1);
        // All 20 failed tests must be listed — set is complete
        for (var i = 0; i < 20; i++)
            Assert.Contains($"Test{i:D2}", actual);
        Assert.DoesNotContain("more", actual);
    }

    [Fact]
    public void Failed_test_detail_shown_in_full()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1, Duration: 1 s");
        sb.AppendLine("  Failed MyTest [1 ms]");
        // More than 5 detail lines
        for (var i = 0; i < 10; i++)
            sb.AppendLine($"    Detail line {i}");
        var actual = new DotnetTestFilter().Apply(sb.ToString(), 1);
        // All 10 detail lines must appear — no 5-line cap
        for (var i = 0; i < 10; i++)
            Assert.Contains($"Detail line {i}", actual);
    }
}
