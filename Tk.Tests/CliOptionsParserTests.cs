using Tk;
using Xunit;

namespace Tk.Tests;

public class CliOptionsParserTests
{
    [Fact]
    public void Defaults_when_no_args()
    {
        var opts = CliOptionsParser.Parse([]);
        Assert.False(opts.Raw);
        Assert.Equal(DetailLevel.Default, opts.DetailLevel);
        Assert.Empty(opts.CommandArgs);
    }

    [Fact]
    public void Raw_flag_sets_raw_true_and_strips_from_command_args()
    {
        var opts = CliOptionsParser.Parse(["--raw", "git", "status"]);
        Assert.True(opts.Raw);
        Assert.Equal(DetailLevel.Default, opts.DetailLevel);
        Assert.Equal(new[] { "git", "status" }, opts.CommandArgs);
    }

    [Fact]
    public void More_flag_sets_detail_level()
    {
        var opts = CliOptionsParser.Parse(["--more", "git", "status"]);
        Assert.False(opts.Raw);
        Assert.Equal(DetailLevel.More, opts.DetailLevel);
        Assert.Equal(new[] { "git", "status" }, opts.CommandArgs);
    }

    [Fact]
    public void Raw_and_more_combine_in_any_order()
    {
        var opts = CliOptionsParser.Parse(["--more", "--raw", "ls"]);
        Assert.True(opts.Raw);
        Assert.Equal(DetailLevel.More, opts.DetailLevel);
        Assert.Equal(new[] { "ls" }, opts.CommandArgs);
    }

    [Fact]
    public void More_after_subcommand_is_recognized_and_stripped()
    {
        // --more is tk-only, so it's recognized even after the subcommand/command args
        // start, and removed from CommandArgs so it isn't forwarded to the tool.
        var opts = CliOptionsParser.Parse(["git", "--more", "status"]);
        Assert.False(opts.Raw);
        Assert.Equal(DetailLevel.More, opts.DetailLevel);
        Assert.Equal(new[] { "git", "status" }, opts.CommandArgs);
    }

    [Fact]
    public void More_after_subcommand_with_other_flags_is_recognized_and_stripped()
    {
        var opts = CliOptionsParser.Parse(["dotnet", "test", "--more", "--filter", "X"]);
        Assert.False(opts.Raw);
        Assert.Equal(DetailLevel.More, opts.DetailLevel);
        Assert.Equal(new[] { "dotnet", "test", "--filter", "X" }, opts.CommandArgs);
    }

    [Fact]
    public void Raw_after_subcommand_is_left_untouched_for_the_underlying_tool()
    {
        // --raw collides with real git options (git diff/show/log --raw), so unlike
        // --more it stays position-sensitive: only a leading --raw sets tk's Raw flag.
        // A --raw appearing after the subcommand is left in CommandArgs and forwarded.
        var opts = CliOptionsParser.Parse(["git", "diff", "--raw"]);
        Assert.False(opts.Raw);
        Assert.Equal(DetailLevel.Default, opts.DetailLevel);
        Assert.Equal(new[] { "git", "diff", "--raw" }, opts.CommandArgs);
    }

    [Fact]
    public void Only_global_flags_with_no_command_yields_empty_command_args()
    {
        var opts = CliOptionsParser.Parse(["--raw"]);
        Assert.True(opts.Raw);
        Assert.Empty(opts.CommandArgs);
    }
}
