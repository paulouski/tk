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
              s:src/b.cs
              s:added.cs
              m:src/a.cs
              u:new.cs

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
              s:staged.cs
              m:modified.cs
              u:untracked.cs

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
              s:new file:   added.cs
              m:modified:   src/a.cs
              u:new.cs

            """,
            actual);
    }

    [Fact]
    public void Top_paths_lists_all_files_by_default()
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
        // All 7 paths must appear — set is complete
        var pathLines = actual.Split('\n').Where(l => l.StartsWith("  m:") || l.StartsWith("  s:") || l.StartsWith("  u:")).ToArray();
        Assert.Equal(7, pathLines.Length);
        Assert.Contains("  m:a.cs", actual);
        Assert.Contains("  m:g.cs", actual);
    }

    [Fact]
    public void Top_paths_lists_all_files_with_more_detail()
    {
        var lines = new List<string> { "## main" };
        for (var i = 0; i < 15; i++)
            lines.Add($" M f{i:00}.cs");
        var raw = string.Join("\n", lines);
        var actual = new GitStatusFilter(DetailLevel.More).Apply(raw, 0);
        Assert.Contains("mod=15", actual);
        // All 15 paths must appear — set is complete
        var pathLines = actual.Split('\n').Where(l => l.StartsWith("  m:") || l.StartsWith("  s:") || l.StartsWith("  u:")).ToArray();
        Assert.Equal(15, pathLines.Length);
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

    [Fact]
    public void State_raw_adds_repository_state_to_summary()
    {
        var raw = "## main\n";
        var stateRaw = """
            On branch main
            You are currently cherry-picking commit abc123.
              (fix conflicts and run "git cherry-pick --continue")
            """;

        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0, stateRaw);

        Assert.Contains("state=cherry-pick", actual);
    }

    // Unity mode tests

    [Fact]
    public void Unity_mode_partitions_meta_files_out_of_counts()
    {
        var raw = """
            ## main
            M  Assets/Foo.cs
            M  Assets/Foo.cs.meta
             M Assets/Bar.shader
             M Assets/Bar.shader.meta
            ?? Assets/New.cs
            ?? Assets/New.cs.meta
            """;

        var actual = new GitStatusFilter(DetailLevel.Default, unityMode: true).Apply(raw, 0);

        Assert.Contains("st=1", actual);
        Assert.Contains("mod=1", actual);
        Assert.Contains("untr=1", actual);
        Assert.Contains("meta=3", actual);
    }

    [Fact]
    public void Unity_mode_meta_paths_absent_from_top_list()
    {
        var raw = """
            ## main
            M  Assets/Foo.cs
            M  Assets/Foo.cs.meta
            ?? Assets/New.cs.meta
            """;

        var actual = new GitStatusFilter(DetailLevel.Default, unityMode: true).Apply(raw, 0);

        // Only one non-meta path should appear in the per-line path list
        var pathLines = actual.Split('\n').Where(l => l.StartsWith("  s:") || l.StartsWith("  m:") || l.StartsWith("  u:")).ToArray();
        Assert.DoesNotContain(pathLines, l => l.Contains(".meta"));
    }

    [Fact]
    public void Unity_mode_meta_only_repo_is_not_ok()
    {
        var raw = """
            ## main
            M  Assets/Foo.cs.meta
            """;

        var actual = new GitStatusFilter(DetailLevel.Default, unityMode: true).Apply(raw, 0);

        Assert.StartsWith("status ", actual);
        Assert.Contains("meta=1", actual);
        Assert.DoesNotContain("ok status", actual);
    }

    [Fact]
    public void Unity_mode_clean_repo_with_no_meta_is_ok()
    {
        var raw = "## main\n";

        var actual = new GitStatusFilter(DetailLevel.Default, unityMode: true).Apply(raw, 0);

        Assert.StartsWith("ok status", actual);
        Assert.DoesNotContain("meta=", actual);
    }

    [Fact]
    public void Default_mode_never_outputs_meta_field()
    {
        var raw = """
            ## main
            M  Assets/Foo.cs.meta
             M Assets/Bar.cs.meta
            ?? Assets/New.cs.meta
            """;

        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0);

        Assert.DoesNotContain("meta=", actual);
        // All .meta paths appear normally in default mode
        Assert.Contains("st=1", actual);
        Assert.Contains("mod=1", actual);
        Assert.Contains("untr=1", actual);
    }
}
