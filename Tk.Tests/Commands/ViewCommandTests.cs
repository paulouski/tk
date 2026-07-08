using Tk.Commands;
using Tk.Commands.View;
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
    public async Task Overload_symbol_with_small_total_body_shows_all_bodies()
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

        var (output, exitCode) = await ViewCommand.RenderAsync($"{path}::Apply", [], ctxRaw: false, ctxMore: false, ct: default);

        Assert.Equal(0, exitCode);
        Assert.Contains("3 matches (showing all)", output);
        Assert.Contains("DoWork(x1)", output);
        Assert.Contains("DoWork(x2)", output);
        Assert.Contains("DoWork(x3)", output);
    }

    [Fact]
    public async Task Overload_symbol_with_large_total_body_shows_first_plus_manifest()
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

        var (output, exitCode) = await ViewCommand.RenderAsync($"{path}::Apply", [], ctxRaw: false, ctxMore: false, ct: default);

        Assert.Equal(0, exitCode);
        Assert.Contains("22 matches (showing 1 of 22)", output);
        Assert.Contains("(22 total)", output);
        Assert.Contains("…", output);
        // First overload's body is still shown in full.
        Assert.Contains("DoWork(x1)", output);
    }

    [Fact]
    public async Task Non_overloaded_symbol_is_unaffected()
    {
        var path = WriteTemp("Foo.cs", "public class Foo\n{\n    public void Solo()\n    {\n        DoWork();\n    }\n}");

        var (output, exitCode) = await ViewCommand.RenderAsync($"{path}::Solo", [], ctxRaw: false, ctxMore: false, ct: default);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("matches", output);
        Assert.Contains("DoWork()", output);
    }

    // ─── symbol/hot list disclosure incl. properties (defect 3) ──────────────

    [Fact]
    public async Task Symbol_summary_includes_properties_and_discloses_truncation()
    {
        var lines = new List<string> { "namespace Test;", "", "public class Foo", "{" };
        for (var i = 1; i <= 50; i++)
            lines.Add($"    public int Prop{i} {{ get; set; }}");
        lines.Add("}");
        var path = WriteTemp("Foo.cs", string.Join('\n', lines));

        var (output, exitCode) = await ViewCommand.RenderAsync(path, [], ctxRaw: false, ctxMore: false, ct: default);

        Assert.Equal(0, exitCode);
        Assert.Contains("Prop1 ", output);
        Assert.Contains("hid=", output);
        Assert.Contains("(--more)", output);
    }

    [Fact]
    public async Task Attribute_prefixed_property_is_detected_as_a_symbol()
    {
        var lines = new List<string> { "public class Invoice", "{" };
        lines.Add("    [JsonInclude] public InvoiceStatus Status { get; private set; }");
        lines.Add("    [JsonInclude] public decimal NetAmount { get; private set; }");
        for (var i = 1; i <= 40; i++)
            lines.Add($"    // filler comment {i} to push this file past the small-file threshold");
        lines.Add("}");
        var path = WriteTemp("Invoice.cs", string.Join('\n', lines));

        var (output, _) = await ViewCommand.RenderAsync(path, [], ctxRaw: false, ctxMore: false, ct: default);

        Assert.Contains("Status ", output);
        Assert.Contains("NetAmount ", output);
    }

    [Fact]
    public async Task More_flag_raises_symbol_cap_and_updates_disclosure_suffix()
    {
        var lines = new List<string> { "namespace Test;", "", "public class Foo", "{" };
        for (var i = 1; i <= 50; i++)
            lines.Add($"    public int Prop{i} {{ get; set; }}");
        lines.Add("}");
        var path = WriteTemp("Foo.cs", string.Join('\n', lines));

        var (defaultOutput, _) = await ViewCommand.RenderAsync(path, [], ctxRaw: false, ctxMore: false, ct: default);
        var (moreOutput, _) = await ViewCommand.RenderAsync(path, [], ctxRaw: false, ctxMore: true, ct: default);

        // More detail should surface strictly more properties by name.
        Assert.DoesNotContain("Prop20 ", defaultOutput);
        Assert.Contains("Prop20 ", moreOutput);
        Assert.Contains("(--raw)", moreOutput);
        Assert.DoesNotContain("(--more)", moreOutput);
    }

    // ─── expression-bodied / arrow boundary detection (defect 4) ─────────────

    [Fact]
    public async Task Expression_bodied_method_does_not_swallow_following_properties()
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

        var (output, exitCode) = await ViewCommand.RenderAsync($"{path}::GetInvoiceId", [], ctxRaw: false, ctxMore: false, ct: default);

        Assert.Equal(0, exitCode);
        Assert.Contains("42", output);
        Assert.DoesNotContain("Status", output);
        Assert.DoesNotContain("get; set;", output);
    }

    [Fact]
    public async Task Short_arrow_function_does_not_swallow_following_statements()
    {
        var source = string.Join('\n',
        [
            "const other = 1;",
            "const shortArrow = (x) => x + 1;",
            "const after = someLongFunctionCallThatShouldNotAppear();"
        ]);
        var path = WriteTemp("app.ts", source);

        var (output, exitCode) = await ViewCommand.RenderAsync($"{path}::shortArrow", [], ctxRaw: false, ctxMore: false, ct: default);

        Assert.Equal(0, exitCode);
        Assert.Contains("x + 1", output);
        Assert.DoesNotContain("someLongFunctionCallThatShouldNotAppear", output);
    }

    [Fact]
    public async Task Block_bodied_arrow_function_ends_at_matching_closing_brace()
    {
        var source = string.Join('\n',
        [
            "const longArrow = (x) => {",
            "    return x + 1;",
            "};",
            "const after = someLongFunctionCallThatShouldNotAppear();"
        ]);
        var path = WriteTemp("app.ts", source);

        var (output, exitCode) = await ViewCommand.RenderAsync($"{path}::longArrow", [], ctxRaw: false, ctxMore: false, ct: default);

        Assert.Equal(0, exitCode);
        Assert.Contains("return x + 1", output);
        Assert.DoesNotContain("someLongFunctionCallThatShouldNotAppear", output);
    }

    // ─── markdown default rendering (defect 5) ────────────────────────────────

    [Fact]
    public async Task Small_markdown_file_shows_full_content_by_default()
    {
        var lines = new List<string> { "# Title", "" };
        for (var i = 0; i < 45; i++)
            lines.Add($"Body paragraph line {i}.");
        var source = string.Join('\n', lines);
        var path = WriteTemp("README.md", source);

        var (output, exitCode) = await ViewCommand.RenderAsync(path, [], ctxRaw: false, ctxMore: false, ct: default);

        Assert.Equal(0, exitCode);
        Assert.Contains("Body paragraph line 0.", output);
        Assert.Contains("Body paragraph line 44.", output);
    }

    [Fact]
    public async Task Large_markdown_file_shows_heading_outline_and_content_preview_with_disclosure()
    {
        var lines = new List<string> { "# Title", "" };
        for (var i = 0; i < 500; i++)
            lines.Add($"Body paragraph line {i}.");
        var source = string.Join('\n', lines);
        var path = WriteTemp("README.md", source);

        var (output, exitCode) = await ViewCommand.RenderAsync(path, [], ctxRaw: false, ctxMore: false, ct: default);

        Assert.Equal(0, exitCode);
        Assert.Contains("headings: Title(1)", output);
        Assert.Contains("Body paragraph line 0.", output);
        Assert.Contains("hid=", output);
    }

    // ─── outline (backlog-2 S-tier) ────────────────────────────────────────────

    [Fact]
    public async Task Default_path_on_large_cs_file_uses_regex_outline_with_source_marker()
    {
        // 50 properties + class + namespace scaffolding → exceeds the 40-line / 3000-char
        // small-file gate, so the new outline path is the one that runs. No daemon in the
        // test environment, so CSharpLspOutlineProvider returns null and the regex fallback
        // wins — this test pins the fallback contract.
        var lines = new List<string> { "namespace Test;", "", "public class Big", "{" };
        for (var i = 1; i <= 50; i++)
            lines.Add($"    public int Prop{i} {{ get; set; }}");
        lines.Add("}");
        var path = WriteTemp("Big.cs", string.Join('\n', lines));

        var (output, exitCode) = await ViewCommand.RenderAsync(path, [], ctxRaw: false, ctxMore: false, ct: default);

        Assert.Equal(0, exitCode);
        Assert.Contains("source=regex approx", output);
        Assert.Contains("symbols=51", output); // 1 type + 50 properties
        Assert.Contains("type Big", output);
        Assert.Contains("property Prop1", output);
        Assert.Contains("body=1", output);
        Assert.Contains("hid=", output);
        Assert.Contains("(--more)", output);
    }

    [Fact]
    public async Task Default_path_outline_is_a_single_flat_list_no_legacy_hot_section()
    {
        // The new outline format unifies "symbols" + "hot" into a single list. This pins
        // that the legacy two-section "hot:" header is gone from the default path. (The
        // legacy format is still reachable via the --symbols flag — see RenderSummary.)
        var lines = new List<string> { "namespace Test;", "", "public class Flat", "{" };
        for (var i = 1; i <= 50; i++)
            lines.Add($"    public int Prop{i} {{ get; set; }}");
        lines.Add("}");
        var path = WriteTemp("Flat.cs", string.Join('\n', lines));

        var (output, _) = await ViewCommand.RenderAsync(path, [], ctxRaw: false, ctxMore: false, ct: default);

        Assert.DoesNotContain("\nhot:", output);
    }

    [Fact]
    public async Task Symbols_flag_preserves_legacy_two_section_format()
    {
        // The --symbols flag keeps the pre-S-tier format for back-compat — this test
        // pins that contract. New default path uses the unified format above; --symbols
        // is the explicit opt-in for the old shape.
        var lines = new List<string> { "namespace Test;", "", "public class Legacy", "{" };
        for (var i = 1; i <= 50; i++)
            lines.Add($"    public int Prop{i} {{ get; set; }}");
        lines.Add("}");
        var path = WriteTemp("Legacy.cs", string.Join('\n', lines));

        var (output, _) = await ViewCommand.RenderAsync(path, ["--symbols"], ctxRaw: false, ctxMore: false, ct: default);

        Assert.Contains("symbols: Legacy(3)", output);
        Assert.Contains("\nhot:", output);
    }

    [Fact]
    public async Task Default_path_uses_lsp_outline_when_provider_returns_hierarchical_symbols()
    {
        // LSP-backed outline: swap the LspProvider for a fake that returns a hierarchical
        // result (class with nested methods) and verify the renderer produces the
        // `source=lsp` format with indented children and the LSP detail in parens. The
        // regex provider is also swapped (to a no-op stub) so the test is hermetic — no
        // fallback path could accidentally satisfy an assertion.
        var lspProvider = new FakeLspOutlineProvider();
        var regexProvider = new FakeRegexOutlineProvider();
        ViewCommand.SetProvidersForTest(lspProvider, regexProvider);
        try
        {
            var lines = new List<string> { "namespace Test;", "", "public class Big", "{" };
            for (var i = 1; i <= 50; i++)
                lines.Add($"    public int Prop{i} {{ get; set; }}");
            lines.Add("}");
            var path = WriteTemp("Big.cs", string.Join('\n', lines));

            var (output, exitCode) = await ViewCommand.RenderAsync(path, [], ctxRaw: false, ctxMore: false, ct: default);

            Assert.Equal(0, exitCode);
            Assert.Contains("source=lsp", output);
            Assert.Contains("class Big", output);
            // Children are indented 2 spaces, detail appears in parens after name.
            Assert.Contains("  method Apply", output);
            Assert.Contains("Apply(int x)", output);
            // The header reports top-level symbol count; the fake has 1 class.
            Assert.Contains("symbols=1", output);
            // Regex fallback should NOT have been called when LSP succeeded.
            Assert.False(regexProvider.Called, "regex provider should not be invoked when LSP succeeds");
        }
        finally
        {
            ViewCommand.ResetProvidersForTest();
        }
    }

    [Fact]
    public async Task Default_path_falls_back_to_regex_when_lsp_provider_returns_null()
    {
        // The fallback contract: a failed LSP path (provider returns null) must hand off
        // to the regex provider transparently — output switches to `source=regex approx`
        // and the user gets an outline either way.
        var lspProvider = new FakeLspOutlineProvider { Result = null };
        var regexProvider = new FakeRegexOutlineProvider();
        ViewCommand.SetProvidersForTest(lspProvider, regexProvider);
        try
        {
            var lines = new List<string> { "namespace Test;", "", "public class Fallback", "{" };
            for (var i = 1; i <= 50; i++)
                lines.Add($"    public int Prop{i} {{ get; set; }}");
            lines.Add("}");
            var path = WriteTemp("Fallback.cs", string.Join('\n', lines));

            var (output, exitCode) = await ViewCommand.RenderAsync(path, [], ctxRaw: false, ctxMore: false, ct: default);

            Assert.Equal(0, exitCode);
            Assert.Contains("source=regex approx", output);
            Assert.True(regexProvider.Called, "regex provider should be invoked when LSP returns null");
        }
        finally
        {
            ViewCommand.ResetProvidersForTest();
        }
    }

    // ─── YAML outline (backlog-2 A-tier #4) ──────────────────────────────────

    [Fact]
    public async Task Default_path_on_large_yaml_workflow_renders_yaml_outline()
    {
        // End-to-end chain: .yml under .github/workflows/ → YamlOutlineProvider handles
        // it, output uses the `source=yaml` tag and the workflow/on/jobs shape from the
        // spec. Filler lines push the file past the 40-line small-file threshold so the
        // outline path runs (small files render whole).
        var dir = Directory.CreateTempSubdirectory().FullName;
        var workflowDir = Path.Combine(dir, ".github", "workflows");
        Directory.CreateDirectory(workflowDir);
        var path = Path.Combine(workflowDir, "ci.yml");
        var lines = new List<string>
        {
            "name: CI",
            "on: push",
            "jobs:",
            "  build:",
            "    runs-on: ubuntu-latest",
            "    steps:",
            "      - uses: actions/checkout@v4",
            "      - run: dotnet build",
        };
        for (var i = 0; i < 50; i++)
            lines.Add($"      - run: echo filler{i}");
        lines.Add("  test:");
        lines.Add("    runs-on: ubuntu-latest");
        lines.Add("    steps:");
        lines.Add("      - run: dotnet test");
        File.WriteAllText(path, string.Join('\n', lines));

        var (output, exitCode) = await ViewCommand.RenderAsync(path, [], ctxRaw: false, ctxMore: false, ct: default);

        Assert.Equal(0, exitCode);
        Assert.Contains("source=yaml", output);
        Assert.Contains("workflow: CI", output);
        Assert.Contains("on: push", output);
        Assert.Contains("jobs:", output);
        Assert.Contains("build ", output);
        Assert.Contains("test ", output);
    }

    [Fact]
    public async Task Default_path_on_large_generic_yaml_renders_yaml_outline_with_keys()
    {
        // Non-workflow YAML: the chain still routes through the YAML provider (CanHandle
        // matches on extension), and the renderer emits top-level keys with line ranges.
        var lines = new List<string>
        {
            "apiVersion: v1",
            "kind: ConfigMap",
            "metadata:",
            "  name: foo",
            "  namespace: bar",
            "data:",
            "  key1: value1",
            "  key2: value2",
        };
        for (var i = 0; i < 50; i++)
            lines.Add($"  pad{i}: x"); // push past 40-line threshold
        var path = WriteTemp("config.yaml", string.Join('\n', lines));

        var (output, exitCode) = await ViewCommand.RenderAsync(path, [], ctxRaw: false, ctxMore: false, ct: default);

        Assert.Equal(0, exitCode);
        Assert.Contains("source=yaml", output);
        Assert.Contains("apiVersion 1-", output);
        Assert.Contains("kind 2-", output);
        Assert.Contains("metadata 3-", output);
        Assert.Contains("data 6-", output);
    }

    // ─── test fakes ───────────────────────────────────────────────────────────

    /// <summary>Stub LSP provider used to exercise the LSP path without a real daemon.</summary>
    private sealed class FakeLspOutlineProvider : IFileOutlineProvider
    {
        public OutlineResult? Result { get; set; } = new OutlineResult("lsp",
        [
            new OutlineEntry("class", "Big", 3, 54, 52, null,
            [
                new OutlineEntry("method", "Apply", 4, 4, 1, "int x", []),
            ]),
        ]);
        public bool Called { get; private set; }

        public bool CanHandle(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
        public Task<OutlineResult?> GetOutlineAsync(string path, CancellationToken ct)
        {
            Called = true;
            return Task.FromResult(Result);
        }
    }

    /// <summary>Stub regex provider; records whether it was called.</summary>
    private sealed class FakeRegexOutlineProvider : IFileOutlineProvider
    {
        public bool Called { get; private set; }
        public bool CanHandle(string path) => true;
        public Task<OutlineResult?> GetOutlineAsync(string path, CancellationToken ct)
        {
            Called = true;
            return Task.FromResult<OutlineResult?>(new OutlineResult("regex approx", []));
        }
    }
}
