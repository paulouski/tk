using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

public class DefFormatterTests
{
    private static LspLocation Loc(string uri, int line, int col) =>
        new(uri, line, col, line, col + 1);

    [Fact]
    public void Empty_locations_returns_zero_count()
    {
        var result = DefFormatter.Format("MyClass", []);
        Assert.Equal("def MyClass n=0", result);
    }

    [Fact]
    public void Real_file_shows_header_and_snippet_gutter()
    {
        // Write a small temp .cs file so File.Exists returns true
        var tmpPath = Path.Combine(Path.GetTempPath(), $"DefFormatterTest_{Guid.NewGuid():N}.cs");
        try
        {
            var content = string.Join(Environment.NewLine,
                "namespace Tmp;",
                "public class Foo",
                "{",
                "    public void Bar() { }",
                "}");
            File.WriteAllText(tmpPath, content);

            var fileUri = new Uri(tmpPath).ToString();
            var loc = Loc(fileUri, 1, 0); // 0-based line 1 = "public class Foo"

            var result = DefFormatter.Format("Foo", [loc]);

            // Header
            Assert.Contains("def Foo n=1", result);
            // file= line shows 1-based position (line 1 → display 2)
            Assert.Contains($"file={tmpPath}:2:1", result);
            // Gutter format: line number followed by '|'
            Assert.Contains("|", result);
            // The actual content from line 1 (0-based) should appear
            Assert.Contains("public class Foo", result);
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    [Fact]
    public void Nonexistent_path_shows_degraded_external_form()
    {
        var bogusUri = "file:///nonexistent/path/Symbol.cs";
        var loc = Loc(bogusUri, 0, 0);

        var result = DefFormatter.Format("Symbol", [loc]);

        Assert.Contains("def Symbol n=1", result);
        Assert.Contains("external — not readable", result);
        Assert.Contains("uri=", result);
    }

    [Fact]
    public void Non_file_uri_shows_degraded_external_form()
    {
        // A non-file URI (e.g. csharp:/metadata/...) will not exist on disk
        var metaUri = "csharp:/metadata/projects/Foo/assemblies/System.Runtime/symbols/String.cs";
        var loc = Loc(metaUri, 5, 0);

        var result = DefFormatter.Format("String", [loc]);

        Assert.Contains("def String n=1", result);
        // uri= prefix with raw uri appears
        Assert.Contains("uri=", result);
        Assert.Contains("external — not readable", result);
    }

    [Fact]
    public void Snippet_stops_at_decompilation_log_marker()
    {
        // Roslyn metadata-as-source files end with a "#if false // Decompilation log" block.
        // The snippet must show the signature and stop before that noise.
        var tmpPath = Path.Combine(Path.GetTempPath(), $"DefFormatterTest_{Guid.NewGuid():N}.cs");
        try
        {
            var content = string.Join(Environment.NewLine,
                "namespace Ext;",
                "public abstract record DomainEvent;",
                "#if false // Decompilation log",
                "'167' items in cache",
                "Resolve: 'System.Runtime'",
                "#endif");
            File.WriteAllText(tmpPath, content);

            var loc = Loc(new Uri(tmpPath).ToString(), 1, 0); // line "public abstract record DomainEvent;"

            var result = DefFormatter.Format("DomainEvent", [loc]);

            Assert.Contains("public abstract record DomainEvent;", result);
            Assert.DoesNotContain("Decompilation log", result);
            Assert.DoesNotContain("items in cache", result);
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    [Fact]
    public void Multiple_locations_all_counted_in_header()
    {
        var loc1 = Loc("file:///nonexistent/A.cs", 0, 0);
        var loc2 = Loc("file:///nonexistent/B.cs", 0, 0);

        var result = DefFormatter.Format("X", [loc1, loc2]);

        Assert.Contains("def X n=2", result);
    }
}
