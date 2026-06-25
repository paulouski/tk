using Tk;
using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

public class FocusCommandTests
{
    private static async Task<(int ExitCode, string Output)> RunAsync(
        string rgOutput, string[] args, DetailLevel detail = DetailLevel.Default)
    {
        var (exit, output, _) = await RunWithRunnerAsync(rgOutput, args, detail);
        return (exit, output);
    }

    private static async Task<(int ExitCode, string Output, FakeProcessRunner Runner)> RunWithRunnerAsync(
        string rgOutput, string[] args, DetailLevel detail = DetailLevel.Default)
    {
        var runner = new FakeProcessRunner().Returns(stdout: rgOutput);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var ctx = new CommandContext(args, detail, raw: false, stdout, stderr, runner);
        var exit = await new FocusCommand().RunAsync(ctx);
        return (exit, stdout.ToString(), runner);
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

        var (_, output) = await RunAsync(rgOutput, ["match"]);

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

        var (_, output) = await RunAsync(rgOutput, ["match"]);

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

        var (_, output) = await RunAsync(rgOutput, ["match"]);

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

        var (_, output) = await RunAsync(rgOutput, ["match", "--code"]);

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

        var (_, output) = await RunAsync(rgOutput, ["match"]);

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

        var (_, output) = await RunAsync(rgOutput, ["match"]);

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

        var (_, output) = await RunAsync(rgOutput, ["match"]);

        var headerLine = output.Split('\n').First();
        Assert.Contains("code=2", headerLine);
        Assert.DoesNotContain("gen=", headerLine);
    }

    [Fact]
    public async Task Multiple_words_produce_one_e_per_word_with_F()
    {
        var (_, _, runner) = await RunWithRunnerAsync(
            RgLine("src/Foo.cs", 1, "auth"), ["auth", "token", "refresh"]);

        var call = runner.Calls[0];
        Assert.Contains("-F", call);
        AssertEFor(call, "auth");
        AssertEFor(call, "token");
        AssertEFor(call, "refresh");
        // Three words → three -e flags.
        Assert.Equal(3, call.Count(a => a == "-e"));
    }

    [Fact]
    public async Task Single_word_produces_old_rg_shape_and_no_terms_annotation()
    {
        var rgOutput = string.Join('\n', [
            RgLine("src/Foo.cs", 1, "match"),
            RgLine("src/Foo.cs", 2, "match"),
        ]);

        var (_, output, runner) = await RunWithRunnerAsync(rgOutput, ["match"]);

        var call = runner.Calls[0];
        Assert.Contains("-F", call);
        AssertEFor(call, "match");
        Assert.Equal(1, call.Count(a => a == "-e"));
        // Default path "." is the last argument.
        Assert.Equal(".", call[^1]);

        Assert.DoesNotContain("terms]", output);
        var codeSection = output.Split('\n').First(l => l.StartsWith("code="));
        Assert.Contains("Foo.cs(2)", codeSection);
    }

    [Fact]
    public async Task Path_flag_sets_rg_path_and_is_not_a_query_word()
    {
        // Path must exist (command rejects nonexistent dirs before invoking rg).
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var (_, _, runner) = await RunWithRunnerAsync(
                RgLine("Foo.cs", 1, "auth"), ["auth", "-p", dir]);

            var call = runner.Calls[0];
            Assert.Equal(dir, call[^1]);
            AssertEFor(call, "auth");
            // The path value must not have become a query term.
            Assert.Equal(1, call.Count(a => a == "-e"));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public async Task Long_path_flag_sets_rg_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var (_, _, runner) = await RunWithRunnerAsync(
                RgLine("Foo.cs", 1, "auth"), ["auth", "--path", dir]);

            var call = runner.Calls[0];
            Assert.Equal(dir, call[^1]);
            Assert.Equal(1, call.Count(a => a == "-e"));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public async Task Coverage_ranks_higher_even_with_fewer_total_matches()
    {
        // File A: 2 distinct query words across 2 matched lines.
        // File B: 1 distinct word but 3 matched lines.
        var rgOutput = string.Join('\n', [
            RgLine("src/A.cs", 1, "auth here"),
            RgLine("src/A.cs", 2, "token here"),
            RgLine("src/B.cs", 1, "auth"),
            RgLine("src/B.cs", 2, "auth"),
            RgLine("src/B.cs", 3, "auth"),
        ]);

        var (_, output) = await RunAsync(rgOutput, ["auth", "token"]);

        var codeSection = output.Split('\n').First(l => l.StartsWith("code="));
        var idxA = codeSection.IndexOf("A.cs", StringComparison.Ordinal);
        var idxB = codeSection.IndexOf("B.cs", StringComparison.Ordinal);
        Assert.True(idxA >= 0 && idxB >= 0);
        Assert.True(idxA < idxB, $"A should rank before B: {codeSection}");
    }

    [Fact]
    public async Task Multi_word_output_shows_terms_coverage_annotation()
    {
        var rgOutput = string.Join('\n', [
            RgLine("src/A.cs", 1, "auth"),
            RgLine("src/A.cs", 2, "token"),
        ]);

        var (_, output) = await RunAsync(rgOutput, ["auth", "token"]);

        var codeSection = output.Split('\n').First(l => l.StartsWith("code="));
        Assert.Contains("A.cs(2)[2 terms]", codeSection);
    }

    private static void AssertEFor(string[] call, string word)
    {
        for (var i = 0; i < call.Length - 1; i++)
        {
            if (call[i] == "-e" && call[i + 1] == word)
                return;
        }

        Assert.Fail($"expected -e {word} in: {string.Join(' ', call)}");
    }
}
