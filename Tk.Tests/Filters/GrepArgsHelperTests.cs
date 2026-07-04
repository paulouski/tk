using Tk.Filters;
using Xunit;

namespace Tk.Tests.Filters;

public class GrepArgsHelperTests
{
    private static readonly Func<string, bool> DirsAre = path => path is "src" or "/repo/src";

    // ─── NeedsRecursiveFlag / EnsureRecursive ────────────────────────────────

    [Fact]
    public void Needs_recursive_when_target_is_directory_and_no_flag_present()
    {
        Assert.True(GrepArgsHelper.NeedsRecursiveFlag(["grep", "pattern", "src"], DirsAre));
    }

    [Fact]
    public void Does_not_need_recursive_when_target_is_a_file()
    {
        Assert.False(GrepArgsHelper.NeedsRecursiveFlag(["grep", "pattern", "Program.cs"], DirsAre));
    }

    [Theory]
    [InlineData("-r")]
    [InlineData("-R")]
    [InlineData("--recursive")]
    public void Does_not_need_recursive_when_flag_already_present(string flag)
    {
        Assert.False(GrepArgsHelper.NeedsRecursiveFlag(["grep", flag, "pattern", "src"], DirsAre));
    }

    [Fact]
    public void Does_not_apply_to_rg_which_is_recursive_by_default()
    {
        Assert.False(GrepArgsHelper.NeedsRecursiveFlag(["rg", "pattern", "src"], DirsAre));
    }

    [Fact]
    public void EnsureRecursive_inserts_dash_r_right_after_command()
    {
        var result = GrepArgsHelper.EnsureRecursive(["grep", "pattern", "src"], DirsAre);

        Assert.Equal(["grep", "-r", "pattern", "src"], result);
    }

    [Fact]
    public void EnsureRecursive_leaves_args_unchanged_when_not_needed()
    {
        var args = new[] { "grep", "pattern", "Program.cs" };
        var result = GrepArgsHelper.EnsureRecursive(args, DirsAre);

        Assert.Same(args, result);
    }

    // ─── WantsOwnHelp ─────────────────────────────────────────────────────────

    [Fact]
    public void WantsOwnHelp_true_for_dash_dash_help()
    {
        Assert.True(GrepArgsHelper.WantsOwnHelp(["grep", "--help"]));
    }

    [Fact]
    public void WantsOwnHelp_true_for_bare_dash_h_with_no_pattern()
    {
        Assert.True(GrepArgsHelper.WantsOwnHelp(["grep", "-h"]));
    }

    [Fact]
    public void WantsOwnHelp_false_for_dash_h_used_as_no_filename_flag_with_pattern()
    {
        // Real grep semantics: -h with a pattern/path means "suppress filenames", not help.
        Assert.False(GrepArgsHelper.WantsOwnHelp(["grep", "-h", "pattern", "src"]));
    }

    [Fact]
    public void WantsOwnHelp_false_for_normal_invocation()
    {
        Assert.False(GrepArgsHelper.WantsOwnHelp(["grep", "pattern", "src"]));
    }

    [Fact]
    public void WantsOwnHelp_false_for_non_grep_command()
    {
        Assert.False(GrepArgsHelper.WantsOwnHelp(["rg", "--help"]));
    }

    [Fact]
    public void HelpText_documents_recursive_default_and_hid_key()
    {
        var text = GrepArgsHelper.HelpText();

        Assert.Contains("Recursive by default", text);
        Assert.Contains("hid=", text);
    }
}
