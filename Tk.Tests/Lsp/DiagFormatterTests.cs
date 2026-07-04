using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

public class DiagFormatterTests
{
    private static FileDiagnostics File(string uri, params LspDiagnostic[] diags) => new(uri, diags);

    private static LspDiagnostic Diag(int line, string severity, string? code, string message) =>
        new(line, 0, line, 5, severity, code, message);

    [Fact]
    public void No_diagnostics_is_ok_with_zero_counts()
    {
        var (output, errorCount) = DiagFormatter.Format([], errorsOnly: false);

        Assert.Equal("ok diag e=0 w=0", output);
        Assert.Equal(0, errorCount);
    }

    [Fact]
    public void Errors_produce_FAIL_status_and_exit_signal()
    {
        var byFile = new[] { File("file:///proj/A.cs", Diag(9, "error", "CS1002", "; expected")) };
        var (output, errorCount) = DiagFormatter.Format(byFile, errorsOnly: false);

        Assert.StartsWith("FAIL diag e=1 w=0", output);
        Assert.Equal(1, errorCount);
    }

    [Fact]
    public void Warnings_alone_stay_ok_status()
    {
        var byFile = new[] { File("file:///proj/A.cs", Diag(3, "warning", "CS0168", "unused variable")) };
        var (output, errorCount) = DiagFormatter.Format(byFile, errorsOnly: false);

        Assert.StartsWith("ok diag e=0 w=1", output);
        Assert.Equal(0, errorCount);
    }

    [Fact]
    public void Diagnostic_line_format_matches_file_line_severity_code_message()
    {
        var byFile = new[] { File("file:///proj/A.cs", Diag(41, "error", "CS1002", "; expected")) };
        var (output, _) = DiagFormatter.Format(byFile, errorsOnly: false);

        // LSP is 0-based; display 1-based (line 41 -> 42)
        Assert.Contains("/proj/A.cs:42 error CS1002: ; expected", output);
    }

    [Fact]
    public void Info_and_hint_severities_are_dropped()
    {
        var byFile = new[]
        {
            File("file:///proj/A.cs",
                Diag(0, "info", "IDE0001", "info diag"),
                Diag(1, "hint", "IDE0002", "hint diag"),
                Diag(2, "error", "CS0001", "real error")),
        };
        var (output, errorCount) = DiagFormatter.Format(byFile, errorsOnly: false);

        Assert.DoesNotContain("info diag", output);
        Assert.DoesNotContain("hint diag", output);
        Assert.Contains("real error", output);
        Assert.Equal(1, errorCount);
    }

    [Fact]
    public void Duplicate_diagnostics_are_deduped()
    {
        var byFile = new[]
        {
            File("file:///proj/A.cs",
                Diag(5, "error", "CS0001", "dup"),
                Diag(5, "error", "CS0001", "dup")),
        };
        var (output, errorCount) = DiagFormatter.Format(byFile, errorsOnly: false);

        Assert.Equal(1, errorCount);
        Assert.StartsWith("FAIL diag e=1", output);
    }

    [Fact]
    public void ErrorsOnly_omits_warnings_from_header_and_body()
    {
        var byFile = new[]
        {
            File("file:///proj/A.cs",
                Diag(1, "error", "CS0001", "boom"),
                Diag(2, "warning", "CS0002", "meh")),
        };
        var (output, errorCount) = DiagFormatter.Format(byFile, errorsOnly: true);

        Assert.StartsWith("FAIL diag e=1", output);
        Assert.DoesNotContain("w=", output);
        Assert.DoesNotContain("meh", output);
        Assert.Equal(1, errorCount);
    }

    [Fact]
    public void Errors_sorted_before_warnings_and_by_file_then_line()
    {
        var byFile = new[]
        {
            File("file:///proj/Z.cs", Diag(1, "warning", "CS0002", "w1")),
            File("file:///proj/A.cs", Diag(9, "error", "CS0001", "e-late")),
            File("file:///proj/A.cs", Diag(1, "error", "CS0001", "e-early")),
        };
        var (output, _) = DiagFormatter.Format(byFile, errorsOnly: false);

        var eEarly = output.IndexOf("e-early", StringComparison.Ordinal);
        var eLate = output.IndexOf("e-late", StringComparison.Ordinal);
        var w1 = output.IndexOf("w1", StringComparison.Ordinal);

        Assert.True(eEarly < eLate, "errors within the same file should be ordered by line");
        Assert.True(eLate < w1, "all errors should appear before warnings");
    }

    [Fact]
    public void Truncated_scope_discloses_cap_in_header()
    {
        var (output, _) = DiagFormatter.Format([], errorsOnly: false, scopedFileCount: 300, totalFileCount: 842);

        Assert.Contains("f=300/842", output);
        Assert.Contains("--more", output);
    }

    [Fact]
    public void Full_scope_does_not_disclose_cap()
    {
        var (output, _) = DiagFormatter.Format([], errorsOnly: false, scopedFileCount: 5, totalFileCount: 5);

        Assert.DoesNotContain("--more", output);
    }
}
