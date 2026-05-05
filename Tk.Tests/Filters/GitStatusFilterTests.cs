using Tk;
using Tk.Filters;
using Xunit;

namespace Tk.Tests.Filters;

public class GitStatusFilterTests
{
    [Fact]
    public void Clean_repo_porcelain_short_form()
    {
        var raw = "## main...origin/main\n";
        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0);
        Assert.Equal("ok status st=0 mod=0 untr=0 br=main\n", actual);
    }

    [Fact]
    public void Empty_output_is_clean_status()
    {
        var actual = new GitStatusFilter(DetailLevel.Default).Apply("", 0);
        Assert.Equal("ok status st=0 mod=0 untr=0\n", actual);
    }

    [Fact]
    public void Porcelain_mixed_changes_default_detail()
    {
        var raw = """
            ## feature/x...origin/feature/x
             M src/a.cs
            M  src/b.cs
            ?? new.cs
            A  added.cs
            """;
        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0);
        Assert.Equal(
            """
            status st=2 mod=1 untr=1 br=feature/x
            top=s:src/b.cs,s:added.cs,m:src/a.cs,u:new.cs

            """,
            actual);
    }

    [Fact]
    public void Porcelain_more_detail_lists_full_sections()
    {
        var raw = """
            ## main
            M  staged.cs
             M modified.cs
            ?? untracked.cs
            """;
        var actual = new GitStatusFilter(DetailLevel.More).Apply(raw, 0);
        Assert.Equal(
            """
            status st=1 mod=1 untr=1 br=main
            top=s:staged.cs,m:modified.cs,u:untracked.cs
            staged:
              staged.cs
            modified:
              modified.cs
            untracked:
              untracked.cs

            """,
            actual);
    }

    [Fact]
    public void Long_form_status_with_branch_and_sections()
    {
        var raw = """
            On branch main
            Your branch is up to date with 'origin/main'.

            Changes to be committed:
              (use "git restore --staged <file>..." to unstage)
            	new file:   added.cs

            Changes not staged for commit:
              (use "git add <file>..." to update what will be committed)
            	modified:   src/a.cs

            Untracked files:
              (use "git add <file>..." to include in what will be committed)
            	new.cs

            """;
        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0);
        Assert.Equal(
            """
            status st=1 mod=1 untr=1 br=main
            top=s:new file:   added.cs,m:modified:   src/a.cs,u:new.cs

            """,
            actual);
    }

    [Fact]
    public void Top_paths_truncated_to_default_max_5()
    {
        var lines = new[]
        {
            "## main",
            " M a.cs",
            " M b.cs",
            " M c.cs",
            " M d.cs",
            " M e.cs",
            " M f.cs",
            " M g.cs"
        };
        var raw = string.Join("\n", lines);
        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0);
        Assert.Contains("mod=7", actual);
        Assert.Contains("top=m:a.cs,m:b.cs,m:c.cs,m:d.cs,m:e.cs\n", actual);
        Assert.DoesNotContain("m:f.cs", actual);
    }

    [Fact]
    public void Top_paths_truncated_to_more_max_12()
    {
        var lines = new List<string> { "## main" };
        for (var i = 0; i < 15; i++)
            lines.Add($" M f{i:00}.cs");
        var raw = string.Join("\n", lines);
        var actual = new GitStatusFilter(DetailLevel.More).Apply(raw, 0);
        Assert.Contains("mod=15", actual);
        var topLine = actual.Split('\n').First(l => l.StartsWith("top="));
        Assert.Equal(12, topLine.Split(',').Length);
    }

    [Fact]
    public void Branch_with_remote_is_sanitised_to_local_only()
    {
        var raw = "## feature/x...origin/feature/x [ahead 2]\n";
        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0);
        Assert.Contains("br=feature/x", actual);
    }

    [Fact]
    public void Non_zero_exit_returns_raw_output()
    {
        var raw = "fatal: not a git repository\n";
        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 128);
        Assert.Equal(raw, actual);
    }
}
