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
    public void Pure_rename_shows_old_arrow_new_with_ren_tag()
    {
        var raw = """
            diff --git a/Old.cs b/New.cs
            similarity index 100%
            rename from Old.cs
            rename to New.cs
            """;
        var actual = new GitDiffFilter(DetailLevel.Default).Apply(raw, 0);
        Assert.Contains("Old.cs->New.cs(+0 -0)[ren]", actual);
    }

    [Fact]
    public void Rename_with_content_change_shows_arrow_and_counts()
    {
        var raw = """
            diff --git a/Old.cs b/New.cs
            similarity index 80%
            rename from Old.cs
            rename to New.cs
            index aaa..bbb 100644
            --- a/Old.cs
            +++ b/New.cs
            @@ -1 +1 @@
            -old line
            +new line
            """;
        var actual = new GitDiffFilter(DetailLevel.Default).Apply(raw, 0);
        Assert.Contains("Old.cs->New.cs(+1 -1)[ren]", actual);
        Assert.Contains("-old line", actual);
        Assert.Contains("+new line", actual);
    }

    [Fact]
    public void Rename_shown_in_show_mode_too()
    {
        var raw = """
            commit abc1234
            Author: Test User <test@example.com>
            Date:   Mon Jan 1 00:00:00 2024 +0000

                Rename Old.cs to New.cs.

            diff --git a/Old.cs b/New.cs
            similarity index 100%
            rename from Old.cs
            rename to New.cs
            """;
        var actual = new GitDiffFilter(DetailLevel.Default, isShow: true).Apply(raw, 0);
        Assert.Contains("Old.cs->New.cs(+0 -0)[ren]", actual);
    }

    // Combined diff ("diff --cc"): the format git emits for unmerged/conflicted paths.
    [Fact]
    public void Combined_diff_cc_is_not_swallowed()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("diff --cc conflict.cs");
        sb.AppendLine("index 1111111,2222222..0000000");
        sb.AppendLine("--- a/conflict.cs");
        sb.AppendLine("+++ b/conflict.cs");
        sb.AppendLine("@@@ -1,3 -1,3 +1,7 @@@");
        sb.AppendLine("  line1");
        sb.AppendLine("++<<<<<<< HEAD");
        sb.AppendLine(" +line_from_head");
        sb.AppendLine("++=======");
        sb.AppendLine(" +line_from_branch");
        sb.AppendLine("++>>>>>>> feature");
        sb.AppendLine("  line3");
        var raw = sb.ToString();

        var actual = new GitDiffFilter(DetailLevel.Default).Apply(raw, 0);

        // Must not report f=0 — the conflict diff is real content, not swallowed.
        Assert.StartsWith("diff f=1 ", actual);
        Assert.Contains("conflict.cs(", actual);
        Assert.Contains("[conflict]", actual);
        Assert.Contains("<<<<<<< HEAD", actual);
        Assert.Contains("line_from_head", actual);
        Assert.Contains("=======", actual);
        Assert.Contains("line_from_branch", actual);
        Assert.Contains(">>>>>>> feature", actual);
        Assert.Contains("line1", actual);
        Assert.Contains("line3", actual);
    }

    [Fact]
    public void Combined_diff_hunk_header_shows_new_range()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("diff --cc conflict.cs");
        sb.AppendLine("index 1111111,2222222..0000000");
        sb.AppendLine("--- a/conflict.cs");
        sb.AppendLine("+++ b/conflict.cs");
        sb.AppendLine("@@@ -1,3 -1,3 +1,7 @@@");
        sb.AppendLine("  line1");
        var raw = sb.ToString();

        var actual = new GitDiffFilter(DetailLevel.Default).Apply(raw, 0);

        Assert.Contains("@@ conflict.cs 1-7", actual);
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
        // Faithful mode: all changed lines must appear
        Assert.Equal(5, actual.Split('\n').Count(l => l.Contains("-old")));
        Assert.Equal(5, actual.Split('\n').Count(l => l.Contains("+new")));
    }

    [Fact]
    public void Summary_mode_caps_hunk_preview()
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
        var actual = new GitDiffFilter(DetailLevel.Default, summary: true).Apply(sb.ToString(), 0);
        // All 5 files must still appear in top=
        var topLine = actual.Split('\n').First(l => l.StartsWith("top="));
        Assert.Equal(5, topLine["top=".Length..].Split(',').Length);
        // Summary mode caps and shows the overflow notice
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

    // INVARIANT: faithful mode never drops or truncates changed lines.
    [Fact]
    public void Faithful_mode_preserves_all_changed_lines_beyond_old_cap()
    {
        // Build a hunk with more added lines than the old _maxLinesPerHunk cap (4 for Default).
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("diff --git a/big.cs b/big.cs");
        sb.AppendLine("index aaa..bbb 100644");
        sb.AppendLine("--- a/big.cs");
        sb.AppendLine("+++ b/big.cs");
        sb.AppendLine("@@ -1,0 +1,10 @@");
        for (var i = 1; i <= 10; i++)
            sb.AppendLine($"+added_line_{i}");

        var actual = new GitDiffFilter(DetailLevel.Default).Apply(sb.ToString(), 0);

        // Every single added line must appear verbatim — none dropped.
        for (var i = 1; i <= 10; i++)
            Assert.Contains($"+added_line_{i}", actual);
    }

    // INVARIANT: faithful mode never truncates long changed lines.
    [Fact]
    public void Faithful_mode_does_not_truncate_long_changed_lines()
    {
        var longContent = new string('x', 200);
        var longLine = "+" + longContent;
        var raw = $"""
            diff --git a/a.cs b/a.cs
            index aaa..bbb 100644
            --- a/a.cs
            +++ b/a.cs
            @@ -1 +1 @@
            {longLine}
            """;
        var actual = new GitDiffFilter(DetailLevel.Default).Apply(raw, 0);
        // The full 201-char line must appear; no truncation to 120 chars.
        Assert.Contains(longLine, actual);
    }

    // Summary mode still truncates long changed lines (old behaviour preserved).
    [Fact]
    public void Summary_mode_truncates_long_changed_line_to_120()
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
        var actual = new GitDiffFilter(DetailLevel.Default, summary: true).Apply(raw, 0);
        var hit = actual.Split('\n').First(l => l.StartsWith("+x"));
        Assert.EndsWith("...", hit);
        // Truncate keeps the first 120 chars of the original line ("+" + 119 'x'), then "...".
        Assert.Equal(120 + 3, hit.Length);
    }

    // git show: commit header/message must appear before the diff section.
    [Fact]
    public void Show_mode_preserves_commit_header_and_message()
    {
        var raw = """
            commit abc1234def5678
            Author: Test User <test@example.com>
            Date:   Mon Jan 1 00:00:00 2024 +0000

                My commit message here.

                Longer description paragraph.

            diff --git a/src/a.cs b/src/a.cs
            index aaa..bbb 100644
            --- a/src/a.cs
            +++ b/src/a.cs
            @@ -1 +1 @@
            -old
            +new
            """;
        var actual = new GitDiffFilter(DetailLevel.Default, isShow: true).Apply(raw, 0);
        Assert.Contains("commit abc1234def5678", actual);
        Assert.Contains("Author: Test User", actual);
        Assert.Contains("My commit message here.", actual);
        Assert.Contains("Longer description paragraph.", actual);
        // Diff section must also be present.
        Assert.Contains("-old", actual);
        Assert.Contains("+new", actual);
    }
}
