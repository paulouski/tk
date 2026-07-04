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

    // Conflict bucket tests

    [Fact]
    public void Conflict_code_UU_adds_conflict_bucket_and_count()
    {
        var raw = "## main\nUU src/App.cs\n";

        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0);

        Assert.Contains("conf=1", actual);
        Assert.Contains("  c:src/App.cs", actual);
    }

    [Theory]
    [InlineData("DD")]
    [InlineData("AU")]
    [InlineData("UD")]
    [InlineData("UA")]
    [InlineData("DU")]
    [InlineData("AA")]
    [InlineData("UU")]
    public void All_conflict_codes_are_flagged(string code)
    {
        var raw = $"## main\n{code} conflicted.cs\n";

        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0);

        Assert.Contains("conf=1", actual);
        Assert.Contains("  c:conflicted.cs", actual);
    }

    [Fact]
    public void Non_conflict_codes_do_not_add_conflict_bucket()
    {
        var raw = "## main\nMM both_modified.cs\n";

        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0);

        Assert.DoesNotContain("conf=", actual);
        Assert.DoesNotContain("c:", actual);
    }

    [Fact]
    public void Conflict_present_repo_is_not_reported_ok()
    {
        var raw = "## main\nUU src/App.cs\n";

        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0);

        Assert.StartsWith("status ", actual);
        Assert.DoesNotContain("ok status", actual);
    }

    // Detached HEAD tests

    [Fact]
    public void Detached_head_shows_short_sha_from_state_raw()
    {
        var raw = "## HEAD (no branch)\n";
        var stateRaw = """
            HEAD detached at 2dc98d7
            nothing to commit, working tree clean
            """;

        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0, stateRaw);

        Assert.Contains("br=HEAD@2dc98d7", actual);
        Assert.DoesNotContain("no_branch", actual);
    }

    [Fact]
    public void Detached_head_without_state_raw_falls_back_to_sanitized_label()
    {
        var raw = "## HEAD (no branch)\n";

        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0);

        Assert.Contains("br=HEAD_(no_branch)", actual);
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

    // Deleted-file bucket tests

    [Fact]
    public void Porcelain_unstaged_delete_gets_its_own_bucket()
    {
        var raw = "## main\n D removed.cs\n";

        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0);

        Assert.Contains("del=1", actual);
        Assert.Contains("  D:removed.cs", actual);
        Assert.DoesNotContain("mod=1", actual);
        Assert.DoesNotContain("  m:removed.cs", actual);
    }

    [Fact]
    public void Porcelain_staged_delete_gets_its_own_bucket()
    {
        var raw = "## main\nD  removed.cs\n";

        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0);

        Assert.Contains("del=1", actual);
        Assert.Contains("  D:removed.cs", actual);
        Assert.DoesNotContain("st=1", actual);
        Assert.DoesNotContain("  s:removed.cs", actual);
    }

    [Fact]
    public void Porcelain_mixed_delete_and_modify_reports_both_buckets()
    {
        var raw = """
            ## main
             D removed.cs
             M edited.cs
            """;

        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0);

        Assert.Contains("mod=1", actual);
        Assert.Contains("del=1", actual);
        Assert.Contains("  m:edited.cs", actual);
        Assert.Contains("  D:removed.cs", actual);
    }

    [Fact]
    public void Repo_with_only_a_deletion_is_not_reported_ok()
    {
        var raw = "## main\n D removed.cs\n";

        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0);

        Assert.StartsWith("status ", actual);
        Assert.DoesNotContain("ok status", actual);
    }

    [Fact]
    public void Long_form_deleted_file_gets_its_own_bucket()
    {
        var raw = """
            On branch main

            Changes not staged for commit:
              (use "git add/rm <file>..." to update what will be committed)
            	deleted:    src/gone.cs

            """;

        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0);

        Assert.Contains("del=1", actual);
        Assert.Contains("  D:deleted:    src/gone.cs", actual);
        Assert.DoesNotContain("mod=1", actual);
    }

    [Fact]
    public void Conflict_codes_are_not_also_double_counted_as_deleted_or_modified()
    {
        var raw = "## main\nDD both_deleted.cs\n";

        var actual = new GitStatusFilter(DetailLevel.Default).Apply(raw, 0);

        Assert.Contains("conf=1", actual);
        Assert.DoesNotContain("del=", actual);
        Assert.Contains("mod=0", actual);
        Assert.Contains("st=0", actual);
    }

    [Fact]
    public void Unity_mode_deleted_meta_file_excluded_from_del_count()
    {
        var raw = """
            ## main
             D Assets/Foo.cs.meta
             D Assets/Bar.cs
            """;

        var actual = new GitStatusFilter(DetailLevel.Default, unityMode: true).Apply(raw, 0);

        Assert.Contains("del=1", actual);
        Assert.Contains("meta=1", actual);
        Assert.Contains("  D:Assets/Bar.cs", actual);
        Assert.DoesNotContain("Assets/Foo.cs.meta", actual);
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
