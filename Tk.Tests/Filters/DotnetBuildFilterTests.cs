using Tk.Filters;
using Xunit;

namespace Tk.Tests.Filters;

public class DotnetBuildFilterTests
{
    [Fact]
    public void Successful_build_with_no_diagnostics_reports_ok_with_project_count()
    {
        var raw = """
            Microsoft (R) Build Engine

              Determining projects to restore...
              Tk -> /repo/tk/bin/Debug/net8.0/tk.dll

            Build succeeded.
                Time Elapsed 00:00:01.23
            """;
        var actual = new DotnetBuildFilter().Apply(raw, 0);
        Assert.StartsWith("ok build p=1", actual);
        Assert.Contains("t=00:00:01.23", actual);
        // No errors/warnings → counts elided
        Assert.DoesNotContain("e=0 w=0", actual);
    }

    [Fact]
    public void Compile_errors_grouped_by_code()
    {
        var raw = """
            Foo.cs(5,10): error CS1002: ; expected [/repo/A.csproj]
            Bar.cs(8,4): error CS1002: ; expected [/repo/A.csproj]
            Baz.cs(1,1): error CS1003: Syntax error [/repo/A.csproj]
                Time Elapsed 00:00:00.50
            """;
        var actual = new DotnetBuildFilter().Apply(raw, 1);
        Assert.StartsWith("FAIL build p=0 e=3 w=0 t=00:00:00.50\n", actual);
        Assert.Contains("Errors:", actual);
        // CS1002 appears twice (grouped)
        Assert.Contains("error CS1002 (x2)", actual);
        // CS1003 appears once → file prefix used
        Assert.Contains("Baz.cs error CS1003", actual);
    }

    [Fact]
    public void Warnings_grouped_separately()
    {
        var raw = """
            Foo.cs(1,1): warning CS0168: Unused variable 'x'
                Time Elapsed 00:00:00.10
            """;
        var actual = new DotnetBuildFilter().Apply(raw, 0);
        Assert.Contains("e=0 w=1", actual);
        Assert.Contains("Warnings:", actual);
        Assert.Contains("warning CS0168", actual);
    }

    [Fact]
    public void Tool_diagnostic_recognised()
    {
        var raw = "MSBUILD : error MSB1009: Project file does not exist.";
        var actual = new DotnetBuildFilter().Apply(raw, 1);
        Assert.Contains("e=1", actual);
        Assert.Contains("MSB1009", actual);
    }

    [Fact]
    public void Failed_build_with_no_parsed_diagnostics_appends_raw_tail()
    {
        var raw = """
            Some unstructured failure
            another line
            yet another
            """;
        var actual = new DotnetBuildFilter().Apply(raw, 1);
        Assert.StartsWith("FAIL build p=0 e=0 w=0\n", actual);
        Assert.Contains("--- raw tail ---", actual);
        Assert.Contains("yet another", actual);
    }

    [Fact]
    public void NuGet_vulnerabilities_partitioned_into_summary()
    {
        var raw = """
            Foo.csproj : warning NU1902: Package 'Newtonsoft.Json' 12.0.0 has a known moderate severity vulnerability https://github.com/advisories/GHSA-xxxx
            Foo.csproj : warning NU1903: Package 'System.Net.Http' 4.0.0 has a known high severity vulnerability https://github.com/advisories/GHSA-yyyy
            """;
        var actual = new DotnetBuildFilter().Apply(raw, 0);
        Assert.Contains("nu=2", actual);
        Assert.Contains("NuGet Vulnerabilities:", actual);
        Assert.Contains("Newtonsoft.Json", actual);
        Assert.Contains("System.Net.Http", actual);
        // Real warnings count is zero — only NuGet vulns
        Assert.Contains("w=0", actual);
    }

    [Fact]
    public void Long_diagnostic_message_truncated()
    {
        var longMsg = new string('m', 200);
        var raw = $"Foo.cs(1,1): error CS9999: {longMsg}";
        var actual = new DotnetBuildFilter().Apply(raw, 1);
        Assert.Contains("...", actual);
        Assert.DoesNotContain(new string('m', 141), actual);
    }
}
