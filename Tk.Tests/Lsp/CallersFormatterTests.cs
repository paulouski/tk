using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

public class CallersFormatterTests
{
    private static LspLocation Loc(string uri, int line, int col) =>
        new(uri, line, col, line, col + 1);

    [Fact]
    public void Empty_callers_returns_zero_counts()
    {
        var result = CallersFormatter.Format("Hold", []);
        Assert.Equal("callers Hold n=0 f=0", result);
    }

    [Fact]
    public void Single_caller_single_site_shows_correct_counts()
    {
        var callers = new[]
        {
            new CallerInfo("Process", "OrderService", "method",
                Loc("file:///proj/OrderService.cs", 5, 0),
                [Loc("file:///proj/OrderService.cs", 12, 4)]),
        };
        var result = CallersFormatter.Format("Hold", callers);

        Assert.Contains("n=1", result);
        Assert.Contains("f=1", result);
    }

    [Fact]
    public void Multiple_call_sites_counted_in_header()
    {
        var callers = new[]
        {
            new CallerInfo("Process", "OrderService", "method",
                Loc("file:///proj/OrderService.cs", 5, 0),
                [
                    Loc("file:///proj/OrderService.cs", 12, 4),
                    Loc("file:///proj/OrderService.cs", 20, 8),
                ]),
            new CallerInfo("Execute", "PaymentService", "method",
                Loc("file:///proj/PaymentService.cs", 3, 0),
                [Loc("file:///proj/PaymentService.cs", 7, 2)]),
        };
        var result = CallersFormatter.Format("Hold", callers);

        // 3 total call sites across 2 files
        Assert.Contains("n=3", result);
        Assert.Contains("f=2", result);
    }

    [Fact]
    public void Call_sites_grouped_by_file()
    {
        var callers = new[]
        {
            new CallerInfo("Process", "OrderService", "method",
                Loc("file:///proj/OrderService.cs", 5, 0),
                [Loc("file:///proj/OrderService.cs", 12, 4)]),
            new CallerInfo("Execute", "PaymentService", "method",
                Loc("file:///proj/PaymentService.cs", 3, 0),
                [Loc("file:///proj/PaymentService.cs", 7, 2)]),
        };
        var result = CallersFormatter.Format("Hold", callers);

        Assert.Contains("OrderService.cs", result);
        Assert.Contains("PaymentService.cs", result);
    }

    [Fact]
    public void Line_numbers_are_one_based()
    {
        // LSP 0-based line 9, char 4 → display 10:5
        var callers = new[]
        {
            new CallerInfo("Process", "OrderService", "method",
                Loc("file:///proj/OrderService.cs", 0, 0),
                [Loc("file:///proj/OrderService.cs", 9, 4)]),
        };
        var result = CallersFormatter.Format("Hold", callers);

        Assert.Contains("10:5", result);
    }

    [Fact]
    public void Caller_name_appears_in_output()
    {
        var callers = new[]
        {
            new CallerInfo("ProcessOrder", "OrderService", "method",
                Loc("file:///proj/OrderService.cs", 0, 0),
                [Loc("file:///proj/OrderService.cs", 5, 2)]),
        };
        var result = CallersFormatter.Format("Hold", callers);

        Assert.Contains("ProcessOrder", result);
    }

    [Fact]
    public void Multiple_files_sorted_alphabetically()
    {
        var callers = new[]
        {
            new CallerInfo("Z", "", "method",
                Loc("file:///proj/Z.cs", 0, 0),
                [Loc("file:///proj/Z.cs", 0, 0)]),
            new CallerInfo("A", "", "method",
                Loc("file:///proj/A.cs", 0, 0),
                [Loc("file:///proj/A.cs", 0, 0)]),
        };
        var result = CallersFormatter.Format("Hold", callers);

        var aIdx = result.IndexOf("A.cs", StringComparison.Ordinal);
        var zIdx = result.IndexOf("Z.cs", StringComparison.Ordinal);
        Assert.True(aIdx < zIdx, "A.cs should appear before Z.cs");
    }

    [Fact]
    public void Caller_with_multiple_sites_in_same_file_all_listed()
    {
        var callers = new[]
        {
            new CallerInfo("Process", "OrderService", "method",
                Loc("file:///proj/OrderService.cs", 0, 0),
                [
                    Loc("file:///proj/OrderService.cs", 2, 0),
                    Loc("file:///proj/OrderService.cs", 5, 0),
                    Loc("file:///proj/OrderService.cs", 10, 0),
                ]),
        };
        var result = CallersFormatter.Format("Hold", callers);

        // All 3 call sites must appear (1-based: lines 3, 6, 11)
        Assert.Contains("3:1", result);
        Assert.Contains("6:1", result);
        Assert.Contains("11:1", result);
    }
}
