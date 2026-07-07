using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

public class SigFormatterTests
{
    [Fact]
    public void Null_hover_reports_no_hover_info()
    {
        var result = SigFormatter.Format("Foo", null);
        Assert.Equal("sig Foo: no hover info", result);
    }

    [Fact]
    public void Blank_contents_reports_no_hover_info()
    {
        var hover = new HoverResult("file:///proj/Foo.cs", 0, 0, "   ");
        var result = SigFormatter.Format("Foo", hover);
        Assert.Equal("sig Foo: no hover info", result);
    }

    [Fact]
    public void Header_includes_symbol_and_position()
    {
        var hover = new HoverResult("file:///proj/Foo.cs", 9, 4, "void Foo()");
        var result = SigFormatter.Format("Foo", hover);

        Assert.StartsWith("sig Foo  /proj/Foo.cs:10:5", result);
    }

    [Fact]
    public void Markdown_code_fences_are_stripped()
    {
        var hover = new HoverResult("file:///proj/Foo.cs", 0, 0,
            "```csharp\nvoid Foo()\n```\nSummary text.");
        var result = SigFormatter.Format("Foo", hover);

        Assert.DoesNotContain("```", result);
        Assert.Contains("void Foo()", result);
        Assert.Contains("Summary text.", result);
    }

    [Fact]
    public void Consecutive_blank_lines_are_collapsed()
    {
        var raw = "```csharp\nvoid Foo()\n```\n\n\n\nSummary.";
        var cleaned = SigFormatter.StripMarkdownNoise(raw);

        Assert.DoesNotContain("\n\n\n", cleaned);
        Assert.Equal("void Foo()\n\nSummary.", cleaned);
    }

    [Fact]
    public void Leading_and_trailing_blank_lines_are_trimmed()
    {
        var raw = "\n\n```csharp\nvoid Foo()\n```\n\n";
        var cleaned = SigFormatter.StripMarkdownNoise(raw);

        Assert.Equal("void Foo()", cleaned);
    }
}
