using Tk;
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
    public void Default_view_includes_assertion_message_between_name_and_stack_frame()
    {
        var raw = """
            Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1, Duration: 1 s
              Failed Tk.Tests.InvoiceTests.Should_format_number [15 ms]
              Error Message:
               Expected invoice.InvoiceNumber to be "FA/123/2026" because the number is generated on save, but found "DELIBERATE_WRONG_VALUE_XYZ".
              Stack Trace:
                 at FluentAssertions.Execution.XUnit2TestFramework.Throw(String message) in /src/FluentAssertions.cs:line 1
                 at Tk.Tests.InvoiceTests.Should_format_number() in /src/InvoiceTests.cs:line 42
                 at System.RuntimeMethodHandle.InvokeMethod()

                Time Elapsed 00:00:00.50
            """;
        var actual = new DotnetTestFilter().Apply(raw, 1);

        Assert.Contains("Tk.Tests.InvoiceTests.Should_format_number", actual);
        Assert.Contains(
            "Expected invoice.InvoiceNumber to be \"FA/123/2026\" because the number is generated on save, but found \"DELIBERATE_WRONG_VALUE_XYZ\".",
            actual);
        // Default view: only the first stack frame, not the rest.
        Assert.Contains("XUnit2TestFramework.Throw", actual);
        Assert.DoesNotContain("InvoiceTests.Should_format_number() in", actual);
        Assert.DoesNotContain("RuntimeMethodHandle.InvokeMethod", actual);
    }

    [Fact]
    public void More_detail_level_includes_fuller_stack_trace()
    {
        var raw = """
            Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1, Duration: 1 s
              Failed Tk.Tests.InvoiceTests.Should_format_number [15 ms]
              Error Message:
               Expected 1 but found 2.
              Stack Trace:
                 at Frame1() in /src/A.cs:line 1
                 at Frame2() in /src/B.cs:line 2
                 at Frame3() in /src/C.cs:line 3

                Time Elapsed 00:00:00.50
            """;
        var actual = new DotnetTestFilter(DetailLevel.More).Apply(raw, 1);

        Assert.Contains("Expected 1 but found 2.", actual);
        Assert.Contains("Frame1()", actual);
        Assert.Contains("Frame2()", actual);
        Assert.Contains("Frame3()", actual);
    }

    [Fact]
    public void Long_assertion_message_is_capped_with_marker()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1, Duration: 1 s");
        sb.AppendLine("  Failed MyTest [1 ms]");
        sb.AppendLine("  Error Message:");
        for (var i = 0; i < 20; i++)
            sb.AppendLine($"   Message line {i}");
        var actual = new DotnetTestFilter().Apply(sb.ToString(), 1);

        for (var i = 0; i < 12; i++)
            Assert.Contains($"Message line {i}", actual);
        for (var i = 12; i < 20; i++)
            Assert.DoesNotContain($"Message line {i}", actual);
        Assert.Contains("…", actual);
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
