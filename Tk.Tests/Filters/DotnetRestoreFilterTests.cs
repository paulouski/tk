using Tk.Filters;
using Xunit;

namespace Tk.Tests.Filters;

public class DotnetRestoreFilterTests
{
    [Fact]
    public void Up_to_date_reports_compactly()
    {
        var raw = """
            Microsoft (R) Build Engine
              Determining projects to restore...
              All projects are up-to-date for restore.
                Time Elapsed 00:00:00.10
            """;
        var actual = new DotnetRestoreFilter().Apply(raw, 0);
        Assert.StartsWith("ok restore up=1 t=00:00:00.10\n", actual);
    }

    [Fact]
    public void Restored_projects_counted()
    {
        var raw = """
              Determining projects to restore...
              Restored /repo/A.csproj (in 1 sec).
              Restored /repo/B.csproj (in 2 sec).
                Time Elapsed 00:00:03.00
            """;
        var actual = new DotnetRestoreFilter().Apply(raw, 0);
        Assert.StartsWith("ok restore p=2 t=00:00:03.00\n", actual);
    }

    [Fact]
    public void Errors_listed_explicitly()
    {
        var raw = """
            /repo/A.csproj : error NU1101: Unable to find package Foo
            """;
        var actual = new DotnetRestoreFilter().Apply(raw, 1);
        Assert.StartsWith("FAIL restore p=0 e=1\n", actual);
        Assert.Contains("Errors:", actual);
        Assert.Contains("NU1101", actual);
    }

    [Fact]
    public void NuGet_vulnerabilities_grouped_with_count()
    {
        // Project suffix `[...]` is stripped before dedup, so warnings from A and B collapse into one entry with count 2.
        var raw = """
              Restored /repo/A.csproj (in 1 sec).
            X.cs : warning NU1902: Package 'X' 1.0.0 has a known moderate severity vulnerability [/repo/A.csproj]
            X.cs : warning NU1902: Package 'X' 1.0.0 has a known moderate severity vulnerability [/repo/B.csproj]
            """;
        var actual = new DotnetRestoreFilter().Apply(raw, 0);
        Assert.Contains("nu=2", actual);
        Assert.Contains("NuGet Vulnerabilities:", actual);
        Assert.Contains("(x2)", actual);
    }

    [Fact]
    public void Failed_restore_with_no_parsed_errors_appends_raw_tail()
    {
        var raw = """
            unstructured failure
            something blew up
            """;
        var actual = new DotnetRestoreFilter().Apply(raw, 1);
        Assert.StartsWith("FAIL restore p=0\n", actual);
        Assert.Contains("--- raw tail ---", actual);
    }
}
