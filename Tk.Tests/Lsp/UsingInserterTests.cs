using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

public class UsingInserterTests
{
    [Fact]
    public void Inserts_after_last_existing_using()
    {
        var text = "using System;\nusing OrderService.Domain;\n\nnamespace OrderService.Data;\n";
        var updated = UsingInserter.AddUsingIfMissing(text, "OrderService.Storage");

        Assert.Equal(
            "using System;\nusing OrderService.Domain;\nusing OrderService.Storage;\n\nnamespace OrderService.Data;\n",
            updated);
    }

    [Fact]
    public void Inserts_at_top_when_no_existing_usings()
    {
        var text = "namespace Foo;\n\npublic class C {}\n";
        var updated = UsingInserter.AddUsingIfMissing(text, "Bar.Baz");

        Assert.Equal("using Bar.Baz;\nnamespace Foo;\n\npublic class C {}\n", updated);
    }

    [Fact]
    public void Returns_text_unchanged_when_using_already_present()
    {
        var text = "using Bar.Baz;\nusing System;\n\nnamespace Foo;\n";
        var updated = UsingInserter.AddUsingIfMissing(text, "Bar.Baz");

        Assert.Same(text, updated); // no-op: no allocation needed for a no-op
    }

    [Fact]
    public void Ignores_using_static_when_checking_for_existing_match()
    {
        var text = "using static System.Math;\n\nnamespace Foo;\n";
        var updated = UsingInserter.AddUsingIfMissing(text, "System.Math");

        // "using static System.Math;" doesn't count as "using System.Math;" — a plain using
        // directive for System.Math should still be added.
        Assert.Contains("using System.Math;\n", updated);
        Assert.Contains("using static System.Math;\n", updated);
    }

    [Fact]
    public void Preserves_crlf_line_endings()
    {
        var text = "using System;\r\n\r\nnamespace Foo;\r\n";
        var updated = UsingInserter.AddUsingIfMissing(text, "Bar.Baz");

        Assert.Equal("using System;\r\nusing Bar.Baz;\r\n\r\nnamespace Foo;\r\n", updated);
    }
}
