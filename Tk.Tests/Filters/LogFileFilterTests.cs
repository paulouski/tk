using Tk.Filters;
using Xunit;

namespace Tk.Tests.Filters;

public class LogFileFilterTests : IDisposable
{
    private readonly string _tempDir;

    public LogFileFilterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tk-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
    }

    private string WriteLog(string content)
    {
        var path = Path.Combine(_tempDir, "service.log");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Missing_file_returns_not_found()
    {
        var actual = LogFileFilter.Apply("/no/such/file.log", []);
        Assert.Equal("File not found: /no/such/file.log\n", actual);
    }

    [Fact]
    public void All_flag_returns_passthrough()
    {
        var path = WriteLog("line1\nline2\nline3\n");
        var actual = LogFileFilter.Apply(path, ["--all"]);
        Assert.Equal("line1\nline2\nline3\n", actual);
    }

    [Fact]
    public void All_flag_with_last_n_takes_tail()
    {
        var path = WriteLog("a\nb\nc\nd\ne\n");
        var actual = LogFileFilter.Apply(path, ["--all", "--last", "2"]);
        Assert.Equal("d\ne\n", actual);
    }

    [Fact]
    public void Filtered_run_keeps_warn_and_above()
    {
        var path = WriteLog("""
            info: Some.Source[0]
                  Application started
            warn: Some.Source[0]
                  Disk almost full
            fail: Some.Source[0]
                  Boom
            """);
        var actual = LogFileFilter.Apply(path, []);
        Assert.Contains("[WRN]", actual);
        Assert.Contains("Disk almost full", actual);
        Assert.Contains("[ERR]", actual);
        Assert.Contains("Boom", actual);
        // Application-started info is filtered as startup noise
        Assert.DoesNotContain("Application started", actual);
    }

    [Fact]
    public void Errors_only_filters_to_fail_crit_error()
    {
        var path = WriteLog("""
            warn: A[0]
                  warning msg
            fail: B[0]
                  failure msg
            """);
        var actual = LogFileFilter.Apply(path, ["--errors"]);
        Assert.Contains("[ERR]", actual);
        Assert.Contains("failure msg", actual);
        Assert.DoesNotContain("[WRN]", actual);
        Assert.DoesNotContain("warning msg", actual);
    }

    [Fact]
    public void Empty_filtered_log_reports_zero()
    {
        var path = WriteLog("info: Microsoft.Hosting.Lifetime[0]\n      Application started\n");
        var actual = LogFileFilter.Apply(path, []);
        Assert.StartsWith("ok log n=0 file=service.log\n", actual);
    }

    [Fact]
    public void Repeated_entries_are_deduplicated_with_count()
    {
        var path = WriteLog("""
            warn: A[0]
                  same warning
            warn: A[0]
                  same warning
            warn: A[0]
                  same warning
            """);
        var actual = LogFileFilter.Apply(path, []);
        Assert.Contains("(x3)", actual);
        Assert.Single(actual.Split('\n'), l => l.Contains("same warning"));
    }

    [Fact]
    public void Last_n_takes_tail_of_filtered_entries()
    {
        var path = WriteLog("""
            warn: A[0]
                  w1
            warn: A[0]
                  w2
            warn: A[0]
                  w3
            """);
        var actual = LogFileFilter.Apply(path, ["--last", "1"]);
        Assert.Contains("w3", actual);
        Assert.DoesNotContain("w1", actual);
        Assert.DoesNotContain("w2", actual);
    }

    [Fact]
    public void Source_is_shortened_to_class_name()
    {
        var path = WriteLog("""
            warn: My.Long.Namespace.WidgetService[0]
                  something
            """);
        var actual = LogFileFilter.Apply(path, []);
        Assert.Contains("WidgetService:", actual);
        Assert.DoesNotContain("My.Long.Namespace", actual);
    }
}
