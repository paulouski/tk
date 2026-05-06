using Tk.Common;
using Xunit;

namespace Tk.Tests.Common;

public class DiagnosticParserTests
{
    [Fact]
    public void Parses_compiler_diagnostic_with_file_line_col_code_message()
    {
        var line = @"/repo/src/Foo.cs(12,34): warning CS8019: Unnecessary using directive.";
        var d = DiagnosticParser.Parse(line);

        Assert.NotNull(d);
        Assert.Equal("/repo/src/Foo.cs", d!.File);
        Assert.Equal(12, d.Line);
        Assert.Equal(34, d.Column);
        Assert.Equal("warning", d.Kind);
        Assert.Equal("CS8019", d.Code);
        Assert.Equal("Unnecessary using directive.", d.Message);
        Assert.Equal("", d.Project);
    }

    [Fact]
    public void Strips_project_suffix_to_filename_without_extension()
    {
        var line = @"/repo/src/Foo.cs(1,1): error CS1002: ; expected [/repo/src/MyApp.csproj]";
        var d = DiagnosticParser.Parse(line);

        Assert.NotNull(d);
        Assert.Equal("error", d!.Kind);
        Assert.Equal("MyApp", d.Project);
        Assert.Equal("; expected", d.Message);
    }

    [Fact]
    public void Parses_tool_diagnostic_without_file_position()
    {
        var line = "MSBUILD : error MSB1009: Project file does not exist.";
        var d = DiagnosticParser.Parse(line);

        Assert.NotNull(d);
        Assert.Equal("MSBUILD", d!.File);
        Assert.Equal(0, d.Line);
        Assert.Equal(0, d.Column);
        Assert.Equal("error", d.Kind);
        Assert.Equal("MSB1009", d.Code);
        Assert.Equal("Project file does not exist.", d.Message);
        Assert.Equal("", d.Project);
    }

    [Fact]
    public void Returns_null_for_non_diagnostic_lines()
    {
        Assert.Null(DiagnosticParser.Parse(""));
        Assert.Null(DiagnosticParser.Parse("Build succeeded."));
        Assert.Null(DiagnosticParser.Parse("    0 Warning(s)"));
        Assert.Null(DiagnosticParser.Parse("Time Elapsed 00:00:01.23"));
    }

    [Fact]
    public void Trims_whitespace_around_file_and_message()
    {
        var line = @"  Foo.cs  (5,6): warning CS0168: variable declared but never used  ";
        var d = DiagnosticParser.Parse(line);

        Assert.NotNull(d);
        Assert.Equal("Foo.cs", d!.File);
        Assert.Equal("variable declared but never used", d.Message);
    }
}
