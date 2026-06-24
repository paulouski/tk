using Tk;
using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

public class FocusCommandTests
{
    private static async Task<(int ExitCode, string Output)> RunAsync(
        string rgOutput, string[] args, DetailLevel detail = DetailLevel.Default)
    {
        var runner = new FakeProcessRunner().Returns(stdout: rgOutput);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var ctx = new CommandContext(args, detail, raw: false, stdout, stderr, runner);
        var exit = await new FocusCommand().RunAsync(ctx);
        return (exit, stdout.ToString());
    }

    /// <summary>Builds a fake rg line: path:lineNo:content.</summary>
    private static string RgLine(string path, int line, string content) =>
        $"{path}:{line}:{content}";

    [Fact]
    public async Task Generated_files_rank_after_code_files_in_default_scope()
    {
        // Generated file has 5 matches, Code file has 1 — Generated should still rank last.
        // Use flat paths (no common directory) so prefix-stripping leaves the directory visible.
        var rgOutput = string.Join('\n', [
            RgLine("Generated/FooGenerated.cs", 1, "match"),
            RgLine("Generated/FooGenerated.cs", 2, "match"),
            RgLine("Generated/FooGenerated.cs", 3, "match"),
            RgLine("Generated/FooGenerated.cs", 4, "match"),
            RgLine("Generated/FooGenerated.cs", 5, "match"),
            RgLine("Domain/RealCode.cs", 1, "match"),
        ]);

        var (_, output) = await RunAsync(rgOutput, ["match", "."]);

        var codeSection = output.Split('\n').FirstOrDefault(l => l.StartsWith("code=")) ?? "";
        Assert.Contains("RealCode.cs", codeSection);
        Assert.DoesNotContain("FooGenerated.cs", codeSection);
    }

    [Fact]
    public async Task Header_shows_gen_count_when_generated_files_exist()
    {
        // Use flat paths so common-prefix stripping leaves directory segments intact.
        var rgOutput = string.Join('\n', [
            RgLine("Generated/Foo.cs", 1, "match"),
            RgLine("Domain/Real.cs", 1, "match"),
        ]);

        var (_, output) = await RunAsync(rgOutput, ["match", "."]);

        var headerLine = output.Split('\n').First();
        Assert.Contains("gen=1", headerLine);
        Assert.Contains("code=1", headerLine);
    }

    [Fact]
    public async Task Generated_files_not_shown_in_code_section_but_still_counted()
    {
        // Single generated file — prefix is the directory, stripped path is just the filename.
        // Filename contains "Generated" so it's still classified correctly.
        var rgOutput = string.Join('\n', [
            RgLine("src/FooGenerated.cs", 1, "match"),
            RgLine("src/FooGenerated.cs", 2, "match"),
        ]);

        var (_, output) = await RunAsync(rgOutput, ["match", "."]);

        var headerLine = output.Split('\n').First();
        Assert.Contains("gen=1", headerLine);
        Assert.Contains("code=0", headerLine);
    }

    [Fact]
    public async Task Generated_files_excluded_from_code_only_scope()
    {
        var rgOutput = string.Join('\n', [
            RgLine("Generated/Foo.cs", 1, "match"),
            RgLine("Domain/Real.cs", 1, "match"),
        ]);

        var (_, output) = await RunAsync(rgOutput, ["match", ".", "--code"]);

        Assert.DoesNotContain("Generated/Foo.cs", output);
        Assert.Contains("Real.cs", output);
    }

    [Fact]
    public async Task Generated_detection_by_directory_segment()
    {
        // Two files share "src/" prefix; after stripping, generated file retains "/generated/" segment.
        var rgOutput = string.Join('\n', [
            RgLine("src/generated/AutoMapper.cs", 1, "match"),
            RgLine("src/Domain/Real.cs", 1, "match"),
        ]);

        var (_, output) = await RunAsync(rgOutput, ["match", "."]);

        var headerLine = output.Split('\n').First();
        Assert.Contains("gen=1", headerLine);
        Assert.Contains("code=1", headerLine);
    }

    [Fact]
    public async Task Generated_detection_by_file_extension()
    {
        // .g.cs extension on the filename (after prefix stripping, filename is visible)
        var rgOutput = string.Join('\n', [
            RgLine("src/Foo.g.cs", 1, "match"),
            RgLine("src/Bar.cs", 1, "match"),
        ]);

        var (_, output) = await RunAsync(rgOutput, ["match", "."]);

        var headerLine = output.Split('\n').First();
        Assert.Contains("gen=1", headerLine);
        Assert.Contains("code=1", headerLine);
    }

    [Fact]
    public async Task Non_generated_code_files_not_classified_as_generated()
    {
        // "GeneratorFactory.cs" contains "Generator" but not "Generated" — should be Code
        var rgOutput = string.Join('\n', [
            RgLine("src/GeneratorFactory.cs", 1, "match"),
            RgLine("src/Bar.cs", 1, "match"),
        ]);

        var (_, output) = await RunAsync(rgOutput, ["match", "."]);

        var headerLine = output.Split('\n').First();
        Assert.Contains("code=2", headerLine);
        Assert.DoesNotContain("gen=", headerLine);
    }
}
