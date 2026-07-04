using Tk;
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
    public void Missing_file_exits_nonzero()
    {
        LogFileFilter.Apply("/no/such/file.log", [], DetailLevel.Default, out var exitCode);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Non_log_file_with_many_lines_warns_and_exits_nonzero()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 15; i++)
            sb.AppendLine($"using System.Line{i};");
        var path = WriteLog(sb.ToString());

        var actual = LogFileFilter.Apply(path, [], DetailLevel.Default, out var exitCode);

        Assert.Contains("warn:", actual);
        Assert.Contains("not an ASP.NET service log", actual);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Small_unparseable_file_stays_ok_with_zero_exit()
    {
        var path = WriteLog("using System;\nnamespace Foo;\n");

        var actual = LogFileFilter.Apply(path, [], DetailLevel.Default, out var exitCode);

        Assert.StartsWith("ok log n=0", actual);
        Assert.Equal(0, exitCode);
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

    [Fact]
    public void Full_stack_trace_preserved_for_kept_errors()
    {
        // Build a fail entry with 10 stack frames — all must appear in output
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("fail: My.Service[0]");
        sb.AppendLine("      Something went wrong");
        sb.AppendLine("System.InvalidOperationException: boom");
        for (var i = 0; i < 10; i++)
            sb.AppendLine($"   at Method{i}() in File.cs:line {i}");
        var path = WriteLog(sb.ToString());
        var actual = LogFileFilter.Apply(path, []);
        // All 10 frames must be present — no 3-frame cap
        for (var i = 0; i < 10; i++)
            Assert.Contains($"Method{i}()", actual);
        Assert.DoesNotContain("more frames", actual);
    }

    [Fact]
    public void Deduplication_collapse_remains_visible_with_count()
    {
        var path = WriteLog("""
            fail: My.Service[0]
                  database error
               at Method1() in File.cs:line 1
               at Method2() in File.cs:line 2
            fail: My.Service[0]
                  database error
               at Method1() in File.cs:line 1
               at Method2() in File.cs:line 2
            """);
        var actual = LogFileFilter.Apply(path, []);
        // Deduplicated: only one entry, but count shown
        Assert.Contains("(x2)", actual);
        Assert.Single(actual.Split('\n'), l => l.Contains("database error"));
    }

    // NormalizeForDedupKey tests

    [Fact]
    public void Normalize_replaces_plain_numbers()
    {
        Assert.Equal("request <num> at <num>", LogFileFilter.NormalizeForDedupKey("request 12345 at 3.14"));
    }

    [Fact]
    public void Normalize_replaces_guids()
    {
        Assert.Equal("id <guid> done",
            LogFileFilter.NormalizeForDedupKey("id 9a4c3a2c-5820-4385-a4b7-83fc98be548d done"));
    }

    [Fact]
    public void Normalize_replaces_quoted_strings()
    {
        Assert.Equal("name <str> ok", LogFileFilter.NormalizeForDedupKey("""name "John Doe" ok"""));
    }

    [Fact]
    public void Normalize_replaces_iso_timestamps()
    {
        Assert.Equal("at <ts> end", LogFileFilter.NormalizeForDedupKey("at 2024-01-02T03:04:05Z end"));
    }

    [Fact]
    public void Normalize_replaces_long_hex_runs()
    {
        Assert.Equal("hash <hex> done", LogFileFilter.NormalizeForDedupKey("hash a1b2c3d4e5f6 done"));
    }

    [Fact]
    public void Normalize_leaves_unicode_text_untouched()
    {
        Assert.Equal("Zażółć gęślą jaźń <num>", LogFileFilter.NormalizeForDedupKey("Zażółć gęślą jaźń 123"));
    }

    [Fact]
    public void Normalize_does_not_affect_message_display_only_the_key()
    {
        // High-cardinality info entries with unique numbers collapse under one displayed
        // (verbatim, first-occurrence) sample plus a repeat count.
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 3; i++)
        {
            sb.AppendLine("info: My.Service[0]");
            sb.AppendLine($"      request for id {1000 + i}");
        }
        var path = WriteLog(sb.ToString());
        var actual = LogFileFilter.Apply(path, [], DetailLevel.More);

        Assert.Contains("request for id 1000 (x3)", actual);
        Assert.DoesNotContain("<num>", actual);
    }

    // Tiering tests (default vs --more)

    private static readonly string[] InfoTierWords =
    [
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel",
        "india", "juliet", "kilo", "lima", "mike", "november", "oscar"
    ];

    [Fact]
    public void Default_tier_caps_info_to_top_groups_by_count()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("warn: Some.Source[0]");
        sb.AppendLine("      keep this warning");
        // Distinct words, not numbers — a trailing number would be normalized away by
        // NormalizeForDedupKey and collapse all 15 into one group, defeating the test.
        foreach (var word in InfoTierWords)
        {
            sb.AppendLine("info: Some.Source[0]");
            sb.AppendLine($"      unique info message {word}");
        }
        for (var i = 0; i < 5; i++)
        {
            sb.AppendLine("info: Some.Source[0]");
            sb.AppendLine("      hot repeated info message");
        }
        var path = WriteLog(sb.ToString());

        var defaultOutput = LogFileFilter.Apply(path, [], DetailLevel.Default);

        Assert.Contains("keep this warning", defaultOutput);
        Assert.Contains("hot repeated info message (x5)", defaultOutput);
        Assert.Equal(15, defaultOutput.Split('\n').Count(l => l.Contains("[INF]")));
        Assert.Contains("i=15/16 (--more)", defaultOutput);
    }

    [Fact]
    public void More_tier_shows_every_info_group_uncapped()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("warn: Some.Source[0]");
        sb.AppendLine("      keep this warning");
        // Distinct words, not numbers — a trailing number would be normalized away by
        // NormalizeForDedupKey and collapse all 15 into one group, defeating the test.
        foreach (var word in InfoTierWords)
        {
            sb.AppendLine("info: Some.Source[0]");
            sb.AppendLine($"      unique info message {word}");
        }
        for (var i = 0; i < 5; i++)
        {
            sb.AppendLine("info: Some.Source[0]");
            sb.AppendLine("      hot repeated info message");
        }
        var path = WriteLog(sb.ToString());

        var moreOutput = LogFileFilter.Apply(path, [], DetailLevel.More);

        Assert.Equal(16, moreOutput.Split('\n').Count(l => l.Contains("[INF]")));
        Assert.DoesNotContain("(--more)", moreOutput);
    }

    [Fact]
    public void Errors_only_is_unaffected_by_info_tiering()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            sb.AppendLine("info: Some.Source[0]");
            sb.AppendLine($"      unique info message {i}");
        }
        sb.AppendLine("fail: Some.Source[0]");
        sb.AppendLine("      boom");
        var path = WriteLog(sb.ToString());

        var actual = LogFileFilter.Apply(path, ["--errors"], DetailLevel.Default);

        Assert.Contains("[ERR]", actual);
        Assert.Contains("boom", actual);
        Assert.DoesNotContain("[INF]", actual);
    }

    // Unparsed/garbage line tests

    [Fact]
    public void Foreign_garbage_line_is_counted_unparsed_and_not_glued()
    {
        var content = "warn: My.Service[0]\n" +
                      "      disk almost full\n" +
                      "####CORRUPTED_BLOB####\n" +
                      "info: My.Service[0]\n" +
                      "      next entry message\n";
        var path = WriteLog(content);

        var actual = LogFileFilter.Apply(path, []);

        Assert.Contains("unparsed=1", actual);
        Assert.DoesNotContain("CORRUPTED_BLOB", actual);
        Assert.Contains("disk almost full", actual);
        Assert.Contains("next entry message", actual);
    }

    [Fact]
    public void Unindented_exception_header_is_not_counted_as_unparsed()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("fail: My.Service[0]");
        sb.AppendLine("      Something went wrong");
        sb.AppendLine("System.InvalidOperationException: boom");
        sb.AppendLine("   at Method0() in File.cs:line 0");
        var path = WriteLog(sb.ToString());

        var actual = LogFileFilter.Apply(path, []);

        Assert.DoesNotContain("unparsed=", actual);
        Assert.Contains("System.InvalidOperationException: boom", actual);
        Assert.Contains("Method0()", actual);
    }
}
