using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

public class RenameEditApplierTests
{
    // ── Single edit ─────────────────────────────────────────────────────────────

    [Fact]
    public void Single_edit_replaces_text()
    {
        var text = "hello world";
        // Replace "world" (chars 6-11 on line 0)
        var edits = new RenameTextEdit[] { new(0, 6, 0, 11, "there") };
        Assert.Equal("hello there", RenameEditApplier.Apply(text, edits));
    }

    [Fact]
    public void Single_edit_at_start_of_file()
    {
        var text = "Foo bar";
        var edits = new RenameTextEdit[] { new(0, 0, 0, 3, "Baz") };
        Assert.Equal("Baz bar", RenameEditApplier.Apply(text, edits));
    }

    [Fact]
    public void Single_edit_at_end_of_file()
    {
        var text = "hello world";
        // Replace "world" which ends at col 11 == text.Length
        var edits = new RenameTextEdit[] { new(0, 6, 0, 11, "earth") };
        Assert.Equal("hello earth", RenameEditApplier.Apply(text, edits));
    }

    [Fact]
    public void Single_edit_longer_replacement_shifts_correctly()
    {
        // "foo" -> "foobar" — result longer than original
        var text = "abc foo xyz";
        var edits = new RenameTextEdit[] { new(0, 4, 0, 7, "foobar") };
        Assert.Equal("abc foobar xyz", RenameEditApplier.Apply(text, edits));
    }

    [Fact]
    public void Single_edit_shorter_replacement_shifts_correctly()
    {
        // "hello" -> "hi" — result shorter than original
        var text = "hello world";
        var edits = new RenameTextEdit[] { new(0, 0, 0, 5, "hi") };
        Assert.Equal("hi world", RenameEditApplier.Apply(text, edits));
    }

    // ── Multiple edits in one file ───────────────────────────────────────────────

    [Fact]
    public void Multiple_edits_applied_correctly_descending()
    {
        // Two replacements on the same line; applying descending ensures earlier offsets remain valid
        var text = "alpha beta gamma";
        // Replace "alpha" (0,0)-(0,5) and "gamma" (0,11)-(0,16)
        var edits = new RenameTextEdit[]
        {
            new(0, 0, 0, 5, "ONE"),
            new(0, 11, 0, 16, "THREE"),
        };
        Assert.Equal("ONE beta THREE", RenameEditApplier.Apply(text, edits));
    }

    [Fact]
    public void Multiple_edits_on_different_lines()
    {
        var text = "line one\nline two\nline three";
        var edits = new RenameTextEdit[]
        {
            new(0, 5, 0, 8, "ONE"),    // "one" on line 0 -> "ONE"
            new(2, 5, 2, 10, "THREE"), // "three" on line 2 -> "THREE"
        };
        Assert.Equal("line ONE\nline two\nline THREE", RenameEditApplier.Apply(text, edits));
    }

    [Fact]
    public void Multiple_edits_same_line_order_independent()
    {
        // Even if caller passes edits in ascending order, the applier must handle descending
        var text = "foo bar baz";
        var edits = new RenameTextEdit[]
        {
            new(0, 8, 0, 11, "QUX"), // "baz" -> "QUX" — edit at position 8
            new(0, 0, 0, 3, "ZAP"),  // "foo" -> "ZAP" — edit at position 0
        };
        // Apply descending: edit at 8 first, then 0 — should yield "ZAP bar QUX"
        Assert.Equal("ZAP bar QUX", RenameEditApplier.Apply(text, edits));
    }

    // ── Line endings ─────────────────────────────────────────────────────────────

    [Fact]
    public void Works_with_lf_line_endings()
    {
        var text = "line1\nline2\nline3";
        var edits = new RenameTextEdit[] { new(1, 0, 1, 5, "LINE2") };
        Assert.Equal("line1\nLINE2\nline3", RenameEditApplier.Apply(text, edits));
    }

    [Fact]
    public void Works_with_crlf_line_endings()
    {
        // \r\n: each line ends with \r\n; offsets include the \r
        var text = "line1\r\nline2\r\nline3";
        // "line2" is on line 1, starts at char 0
        var edits = new RenameTextEdit[] { new(1, 0, 1, 5, "LINE2") };
        Assert.Equal("line1\r\nLINE2\r\nline3", RenameEditApplier.Apply(text, edits));
    }

    [Fact]
    public void Edit_spanning_crlf_boundary_preserved()
    {
        // Whole file replacement (0,0)-(2,5) over a CRLF file
        var text = "ab\r\ncd\r\nef";
        var edits = new RenameTextEdit[] { new(0, 0, 2, 2, "XY") };
        Assert.Equal("XY", RenameEditApplier.Apply(text, edits));
    }

    // ── No edits ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_edits_returns_original_text()
    {
        var text = "unchanged";
        Assert.Equal("unchanged", RenameEditApplier.Apply(text, []));
    }

    // ── Out-of-range throws ───────────────────────────────────────────────────────

    [Fact]
    public void Out_of_range_line_throws()
    {
        var text = "hello";
        var edits = new RenameTextEdit[] { new(99, 0, 99, 1, "x") };
        Assert.Throws<ArgumentOutOfRangeException>(() => RenameEditApplier.Apply(text, edits));
    }

    [Fact]
    public void Out_of_range_character_throws()
    {
        var text = "hello";
        var edits = new RenameTextEdit[] { new(0, 0, 0, 99, "x") };
        Assert.Throws<ArgumentOutOfRangeException>(() => RenameEditApplier.Apply(text, edits));
    }
}

public class RenameFormatterTests
{
    [Fact]
    public void Empty_files_returns_zero_counts()
    {
        var result = RenameFormatter.Format("OldName", "NewName", []);
        Assert.Equal("rename OldName -> NewName n=0 f=0", result);
    }

    [Fact]
    public void Single_file_single_edit_header_correct()
    {
        var files = new FileEdits[]
        {
            new("file:///proj/Foo.cs", [new(9, 4, 9, 11, "NewName")])
        };
        var result = RenameFormatter.Format("OldName", "NewName", files);
        Assert.Contains("rename OldName -> NewName n=1 f=1", result);
    }

    [Fact]
    public void Multiple_files_sorted_alphabetically()
    {
        var files = new FileEdits[]
        {
            new("file:///proj/Z.cs", [new(0, 0, 0, 3, "X")]),
            new("file:///proj/A.cs", [new(0, 0, 0, 3, "X")]),
        };
        var result = RenameFormatter.Format("Old", "New", files);
        var aIdx = result.IndexOf("A.cs", StringComparison.Ordinal);
        var zIdx = result.IndexOf("Z.cs", StringComparison.Ordinal);
        Assert.True(aIdx < zIdx, "A.cs should appear before Z.cs");
    }

    [Fact]
    public void Line_numbers_are_one_based()
    {
        // LSP 0-based line 9 -> display 10
        var files = new FileEdits[]
        {
            new("file:///proj/Foo.cs", [new(9, 4, 9, 11, "NewName")])
        };
        var result = RenameFormatter.Format("OldName", "NewName", files);
        Assert.Contains("10:5", result);
    }

    [Fact]
    public void Total_edit_count_spans_multiple_files()
    {
        var files = new FileEdits[]
        {
            new("file:///proj/A.cs", [new(0, 0, 0, 3, "X"), new(1, 0, 1, 3, "X")]),
            new("file:///proj/B.cs", [new(0, 0, 0, 3, "X")]),
        };
        var result = RenameFormatter.Format("Old", "New", files);
        Assert.Contains("n=3", result);
        Assert.Contains("f=2", result);
    }
}

public class LspCommandHelpersTests
{
    [Fact]
    public void TryParsePosition_valid_path_returns_true()
    {
        var ok = Tk.Commands.LspCommandHelpers.TryParsePosition("a/b.cs:10:5", out var file, out var line, out var col);
        Assert.True(ok);
        Assert.Equal("a/b.cs", file);
        Assert.Equal(9, line);  // 0-based
        Assert.Equal(4, col);   // 0-based
    }

    [Fact]
    public void TryParsePosition_bare_symbol_returns_false()
    {
        var ok = Tk.Commands.LspCommandHelpers.TryParsePosition("MySymbol", out _, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryParsePosition_single_colon_returns_false()
    {
        var ok = Tk.Commands.LspCommandHelpers.TryParsePosition("foo:10", out _, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryParsePosition_non_numeric_returns_false()
    {
        var ok = Tk.Commands.LspCommandHelpers.TryParsePosition("foo.cs:abc:def", out _, out _, out _);
        Assert.False(ok);
    }
}
