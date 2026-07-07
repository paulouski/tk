using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

public class FixFormatterTests
{
    [Fact]
    public void Unsupported_without_summary_reports_plainly()
    {
        var result = FixFormatter.FormatUnsupported("Foo.cs", null);
        Assert.Equal("fix Foo.cs: unsupported by server", result);
    }

    [Fact]
    public void Unsupported_with_note_appends_it()
    {
        var summary = new FixSummary(false, 0, 0, "server offered no matching quick fix");
        var result = FixFormatter.FormatUnsupported("Foo.cs", summary);

        Assert.Equal("fix Foo.cs: unsupported by server — server offered no matching quick fix", result);
    }

    [Fact]
    public void Nothing_to_fix_reports_zero_counts()
    {
        var result = FixFormatter.FormatNothingToFix("Foo.cs");
        Assert.Equal("ok fix Foo.cs: nothing to fix (added=0 removed=0)", result);
    }

    [Fact]
    public void Applied_reports_added_and_removed_counts()
    {
        var summary = new FixSummary(true, 2, 1, null);
        var result = FixFormatter.FormatApplied("Foo.cs", summary);

        Assert.Equal("ok fix Foo.cs: added=2 removed=1", result);
    }
}
