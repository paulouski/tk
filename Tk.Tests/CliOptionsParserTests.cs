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
    public void Parsing_stops_at_first_non_flag_token()
    {
        // --more after the command is part of CommandArgs (parser stops at first non-flag)
        var opts = CliOptionsParser.Parse(["git", "--more", "status"]);
        Assert.False(opts.Raw);
        Assert.Equal(DetailLevel.Default, opts.DetailLevel);
        Assert.Equal(new[] { "git", "--more", "status" }, opts.CommandArgs);
    }

    [Fact]
    public void Only_global_flags_with_no_command_yields_empty_command_args()
    {
        var opts = CliOptionsParser.Parse(["--raw"]);
        Assert.True(opts.Raw);
        Assert.Empty(opts.CommandArgs);
    }
}
