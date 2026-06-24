using Tk;
using Tk.Filters;
using Xunit;

namespace Tk.Tests.Filters;

public class GitDiffFilterTests
{
    [Fact]
    public void Non_zero_exit_returns_raw()
    {
        var raw = "fatal: bad object\n";
        Assert.Equal(raw, new GitDiffFilter(DetailLevel.Default).Apply(raw, 1));
    }

    [Fact]
    public void Empty_returns_zero_files_summary()
    {
        Assert.Equal("ok diff f=0\n", new GitDiffFilter(DetailLevel.Default).Apply("", 0));
    }

    [Fact]
    public void Single_file_diff_summary_and_hunk_preview()
    {
        var raw = """
            diff --git a/src/a.cs b/src/a.cs
            index aaa..bbb 100644
            --- a/src/a.cs
            +++ b/src/a.cs
            @@ -10,3 +10,4 @@ namespace X
             ctx
            -old
            +new1
            +new2
            """;
        var actual = new GitDiffFilter(DetailLevel.Default).Apply(raw, 0);
        Assert.StartsWith("diff f=1 +2 -1\n", actual);
        Assert.Contains("top=a.cs(+2 -1)\n", actual);
        Assert.Contains("@@ a.cs 10-13", actual);
        Assert.Contains("-old", actual);
        Assert.Contains("+new1", actual);
        Assert.Contains("+new2", actual);
    }

    [Fact]
    public void New_file_marker_in_top()
    {
        var raw = """
            diff --git a/new.cs b/new.cs
            new file mode 100644
            index 0000000..aaa
            --- /dev/null
            +++ b/new.cs
            @@ -0,0 +1,2 @@
            +line1
            +line2
            """;
        var actual = new GitDiffFilter(DetailLevel.Default).Apply(raw, 0);
        Assert.Contains("new.cs(+2 -0)[*new]", actual);
    }

    [Fact]
    public void Deleted_file_marker_in_top()
    {
        var raw = """
            diff --git a/gone.cs b/gone.cs
            deleted file mode 100644
            index aaa..0000000
            --- a/gone.cs
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -line1
            -line2
            """;
        var actual = new GitDiffFilter(DetailLevel.Default).Apply(raw, 0);
        Assert.Contains("gone.cs(+0 -2)[del]", actual);
    }

    [Fact]
    public void Binary_file_counted_in_summary()
    {
        var raw = """
            diff --git a/img.png b/img.png
            index aaa..bbb
            Binary files a/img.png and b/img.png differ
            """;
        var actual = new GitDiffFilter(DetailLevel.Default).Apply(raw, 0);
        Assert.Contains("bin=1", actual);
        Assert.Contains("img.png(+0 -0)[bin]", actual);
    }

    [Fact]
    public void Top_lists_all_files_set_is_complete()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 5; i++)
        {
            sb.AppendLine($"diff --git a/f{i}.cs b/f{i}.cs");
            sb.AppendLine("index aaa..bbb 100644");
            sb.AppendLine($"--- a/f{i}.cs");
            sb.AppendLine($"+++ b/f{i}.cs");
            sb.AppendLine("@@ -1 +1 @@");
            sb.AppendLine("-old");
            sb.AppendLine("+new");
        }
        var actual = new GitDiffFilter(DetailLevel.Default).Apply(sb.ToString(), 0);
        // All 5 files must appear in top= — set is complete
        var topLine = actual.Split('\n').First(l => l.StartsWith("top="));
        Assert.Equal(5, topLine["top=".Length..].Split(',').Length);
        // Hunk preview is bounded; overflow is explicit
        Assert.Contains("hunks more", actual);
    }

    [Fact]
    public void Context_lines_shown_around_changed_lines()
    {
        var raw = """
            diff --git a/src/a.cs b/src/a.cs
            index aaa..bbb 100644
            --- a/src/a.cs
            +++ b/src/a.cs
            @@ -10,5 +10,5 @@ namespace X
             ctx_before
            -old
            +new
             ctx_after
             ctx_after2
            """;
        var actual = new GitDiffFilter(DetailLevel.Default).Apply(raw, 0);
        Assert.Contains("ctx_before", actual);
        Assert.Contains("ctx_after", actual);
    }

    [Fact]
    public void Long_changed_line_truncated_to_120()
    {
        var longLine = "+" + new string('x', 200);
        var raw = $"""
            diff --git a/a.cs b/a.cs
            index aaa..bbb 100644
            --- a/a.cs
            +++ b/a.cs
            @@ -1 +1 @@
            {longLine}
            """;
        var actual = new GitDiffFilter(DetailLevel.Default).Apply(raw, 0);
        var hit = actual.Split('\n').First(l => l.StartsWith("+x"));
        Assert.EndsWith("...", hit);
        // Truncate keeps the first 120 chars of the original line ("+" + 119 'x'), then "...".
        Assert.Equal(120 + 3, hit.Length);
    }
}
