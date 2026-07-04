using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

public class ViewCommandTests
{
    private static string WriteTemp(string fileName, string content)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    // ─── overload / repeated-symbol disclosure (defect 2) ────────────────────

    [Fact]
    public void Overload_symbol_with_small_total_body_shows_all_bodies()
    {
        var lines = new List<string> { "public class Foo", "{" };
        for (var i = 1; i <= 3; i++)
        {
            lines.Add($"    public void Apply(int x{i})");
            lines.Add("    {");
            lines.Add($"        DoWork(x{i});");
            lines.Add("    }");
        }
        lines.Add("}");
        var path = WriteTemp("Foo.cs", string.Join('\n', lines));

        var (output, exitCode) = ViewCommand.Render($"{path}::Apply", [], ctxRaw: false, ctxMore: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("3 matches (showing all)", output);
        Assert.Contains("DoWork(x1)", output);
        Assert.Contains("DoWork(x2)", output);
        Assert.Contains("DoWork(x3)", output);
    }

    [Fact]
    public void Overload_symbol_with_large_total_body_shows_first_plus_manifest()
    {
        var lines = new List<string> { "public class Foo", "{" };
        for (var i = 1; i <= 22; i++)
        {
            lines.Add($"    public void Apply(int x{i})");
            lines.Add("    {");
            lines.Add($"        DoWork(x{i});");
            lines.Add("    }");
        }
        lines.Add("}");
        var path = WriteTemp("Foo.cs", string.Join('\n', lines));

        var (output, exitCode) = ViewCommand.Render($"{path}::Apply", [], ctxRaw: false, ctxMore: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("22 matches (showing 1 of 22)", output);
        Assert.Contains("(22 total)", output);
        Assert.Contains("…", output);
        // First overload's body is still shown in full.
        Assert.Contains("DoWork(x1)", output);
    }

    [Fact]
    public void Non_overloaded_symbol_is_unaffected()
    {
        var path = WriteTemp("Foo.cs", "public class Foo\n{\n    public void Solo()\n    {\n        DoWork();\n    }\n}");

        var (output, exitCode) = ViewCommand.Render($"{path}::Solo", [], ctxRaw: false, ctxMore: false);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("matches", output);
        Assert.Contains("DoWork()", output);
    }

    // ─── symbol/hot list disclosure incl. properties (defect 3) ──────────────

    [Fact]
    public void Symbol_summary_includes_properties_and_discloses_truncation()
    {
        var lines = new List<string> { "namespace Test;", "", "public class Foo", "{" };
        for (var i = 1; i <= 50; i++)
            lines.Add($"    public int Prop{i} {{ get; set; }}");
        lines.Add("}");
        var path = WriteTemp("Foo.cs", string.Join('\n', lines));

        var (output, exitCode) = ViewCommand.Render(path, [], ctxRaw: false, ctxMore: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("Prop1(", output);
        Assert.Contains("hid=", output);
        Assert.Contains("(--more, --raw)", output);
    }

    [Fact]
    public void Attribute_prefixed_property_is_detected_as_a_symbol()
    {
        var lines = new List<string> { "public class Invoice", "{" };
        lines.Add("    [JsonInclude] public InvoiceStatus Status { get; private set; }");
        lines.Add("    [JsonInclude] public decimal NetAmount { get; private set; }");
        for (var i = 1; i <= 40; i++)
            lines.Add($"    // filler comment {i} to push this file past the small-file threshold");
        lines.Add("}");
        var path = WriteTemp("Invoice.cs", string.Join('\n', lines));

        var (output, _) = ViewCommand.Render(path, [], ctxRaw: false, ctxMore: false);

        Assert.Contains("Status(", output);
        Assert.Contains("NetAmount(", output);
    }

    [Fact]
    public void More_flag_raises_symbol_cap_and_updates_disclosure_suffix()
    {
        var lines = new List<string> { "namespace Test;", "", "public class Foo", "{" };
        for (var i = 1; i <= 50; i++)
            lines.Add($"    public int Prop{i} {{ get; set; }}");
        lines.Add("}");
        var path = WriteTemp("Foo.cs", string.Join('\n', lines));

        var (defaultOutput, _) = ViewCommand.Render(path, [], ctxRaw: false, ctxMore: false);
        var (moreOutput, _) = ViewCommand.Render(path, [], ctxRaw: false, ctxMore: true);

        // More detail should surface strictly more properties by name.
        Assert.DoesNotContain("Prop20(", defaultOutput);
        Assert.Contains("Prop20(", moreOutput);
        Assert.Contains("(--raw)", moreOutput);
        Assert.DoesNotContain("(--more, --raw)", moreOutput);
    }

    // ─── expression-bodied / arrow boundary detection (defect 4) ─────────────

    [Fact]
    public void Expression_bodied_method_does_not_swallow_following_properties()
    {
        var source = string.Join('\n',
        [
            "public class Invoice",
            "{",
            "    public int GetInvoiceId() => 42;",
            "",
            "    public string Name { get; set; }",
            "    public string Status { get; set; }",
            "}"
        ]);
        var path = WriteTemp("Invoice.cs", source);

        var (output, exitCode) = ViewCommand.Render($"{path}::GetInvoiceId", [], ctxRaw: false, ctxMore: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("42", output);
        Assert.DoesNotContain("Status", output);
        Assert.DoesNotContain("get; set;", output);
    }

    [Fact]
    public void Short_arrow_function_does_not_swallow_following_statements()
    {
        var source = string.Join('\n',
        [
            "const other = 1;",
            "const shortArrow = (x) => x + 1;",
            "const after = someLongFunctionCallThatShouldNotAppear();"
        ]);
        var path = WriteTemp("app.ts", source);

        var (output, exitCode) = ViewCommand.Render($"{path}::shortArrow", [], ctxRaw: false, ctxMore: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("x + 1", output);
        Assert.DoesNotContain("someLongFunctionCallThatShouldNotAppear", output);
    }

    [Fact]
    public void Block_bodied_arrow_function_ends_at_matching_closing_brace()
    {
        var source = string.Join('\n',
        [
            "const longArrow = (x) => {",
            "    return x + 1;",
            "};",
            "const after = someLongFunctionCallThatShouldNotAppear();"
        ]);
        var path = WriteTemp("app.ts", source);

        var (output, exitCode) = ViewCommand.Render($"{path}::longArrow", [], ctxRaw: false, ctxMore: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("return x + 1", output);
        Assert.DoesNotContain("someLongFunctionCallThatShouldNotAppear", output);
    }

    // ─── markdown default rendering (defect 5) ────────────────────────────────

    [Fact]
    public void Small_markdown_file_shows_full_content_by_default()
    {
        var lines = new List<string> { "# Title", "" };
        for (var i = 0; i < 45; i++)
            lines.Add($"Body paragraph line {i}.");
        var source = string.Join('\n', lines);
        var path = WriteTemp("README.md", source);

        var (output, exitCode) = ViewCommand.Render(path, [], ctxRaw: false, ctxMore: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("Body paragraph line 0.", output);
        Assert.Contains("Body paragraph line 44.", output);
    }

    [Fact]
    public void Large_markdown_file_shows_heading_outline_and_content_preview_with_disclosure()
    {
        var lines = new List<string> { "# Title", "" };
        for (var i = 0; i < 500; i++)
            lines.Add($"Body paragraph line {i}.");
        var source = string.Join('\n', lines);
        var path = WriteTemp("README.md", source);

        var (output, exitCode) = ViewCommand.Render(path, [], ctxRaw: false, ctxMore: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("headings: Title(1)", output);
        Assert.Contains("Body paragraph line 0.", output);
        Assert.Contains("hid=", output);
    }
}
