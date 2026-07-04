using Tk;
using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

public class CommandContextTests
{
    [Fact]
    public void FromCli_strips_command_name_from_args()
    {
        var cli = new CliOptions(Raw: false, DetailLevel: DetailLevel.Default,
            CommandArgs: ["view", "Program.cs", "--symbols"]);
        var ctx = CommandContext.FromCli(cli, TextWriter.Null, TextWriter.Null);

        Assert.Equal(new[] { "Program.cs", "--symbols" }, ctx.Args);
    }

    [Fact]
    public void FromCli_propagates_global_flags()
    {
        var cli = new CliOptions(Raw: true, DetailLevel: DetailLevel.More,
            CommandArgs: ["focus", "needle"]);
        var ctx = CommandContext.FromCli(cli, TextWriter.Null, TextWriter.Null);

        Assert.True(ctx.Raw);
        Assert.Equal(DetailLevel.More, ctx.DetailLevel);
    }

    [Fact]
    public void FromCli_with_only_command_name_yields_empty_args()
    {
        var cli = new CliOptions(Raw: false, DetailLevel: DetailLevel.Default,
            CommandArgs: ["changes"]);
        var ctx = CommandContext.FromCli(cli, TextWriter.Null, TextWriter.Null);

        Assert.Empty(ctx.Args);
    }

    [Fact]
    public void FromCli_with_no_command_args_yields_empty_args()
    {
        var cli = new CliOptions(Raw: false, DetailLevel: DetailLevel.Default,
            CommandArgs: []);
        var ctx = CommandContext.FromCli(cli, TextWriter.Null, TextWriter.Null);

        Assert.Empty(ctx.Args);
    }

    // ─── CommandName / Operands / OriginalCommandArgs ──────────────────────────
    // These document the clarified argument shape: CommandName is the leading token,
    // Operands is everything after it (== Args), and OriginalCommandArgs is the two
    // recombined — the only thing that should ever be handed to a spawned process.

    [Fact]
    public void FromCli_plain_command_has_no_operands()
    {
        var cli = new CliOptions(Raw: false, DetailLevel: DetailLevel.Default,
            CommandArgs: ["changes"]);
        var ctx = CommandContext.FromCli(cli, TextWriter.Null, TextWriter.Null);

        Assert.Equal("changes", ctx.CommandName);
        Assert.Empty(ctx.Operands);
        Assert.Equal(["changes"], ctx.OriginalCommandArgs);
    }

    [Fact]
    public void FromCli_command_with_operands_splits_name_from_operands()
    {
        var cli = new CliOptions(Raw: false, DetailLevel: DetailLevel.Default,
            CommandArgs: ["view", "Program.cs", "--symbols"]);
        var ctx = CommandContext.FromCli(cli, TextWriter.Null, TextWriter.Null);

        Assert.Equal("view", ctx.CommandName);
        Assert.Equal(["Program.cs", "--symbols"], ctx.Operands);
        Assert.Equal(["Program.cs", "--symbols"], ctx.Args);
        Assert.Equal(["view", "Program.cs", "--symbols"], ctx.OriginalCommandArgs);
    }

    [Fact]
    public void FromCli_leading_tk_flags_are_extracted_before_command_name()
    {
        var cli = CliOptionsParser.Parse(["--more", "--raw", "git", "log"]);
        var ctx = CommandContext.FromCli(cli, TextWriter.Null, TextWriter.Null);

        Assert.True(ctx.Raw);
        Assert.Equal(DetailLevel.More, ctx.DetailLevel);
        Assert.Equal("git", ctx.CommandName);
        Assert.Equal(["log"], ctx.Operands);
        Assert.Equal(["git", "log"], ctx.OriginalCommandArgs);
    }

    [Fact]
    public void FromCli_tk_flag_after_command_name_is_not_extracted_and_stays_an_operand()
    {
        // CliOptionsParser only recognizes --more/--raw while scanning leading tokens; once it
        // hits the command name it stops, so a later "--raw" belongs to the wrapped command.
        var cli = CliOptionsParser.Parse(["git", "log", "--raw"]);
        var ctx = CommandContext.FromCli(cli, TextWriter.Null, TextWriter.Null);

        Assert.False(ctx.Raw);
        Assert.Equal("git", ctx.CommandName);
        Assert.Equal(["log", "--raw"], ctx.Operands);
        Assert.Equal(["git", "log", "--raw"], ctx.OriginalCommandArgs);
    }

    [Fact]
    public void FromCli_git_dash_capital_c_global_flag_stays_in_operands_and_order()
    {
        var cli = new CliOptions(Raw: false, DetailLevel: DetailLevel.Default,
            CommandArgs: ["git", "-C", "/path", "status"]);
        var ctx = CommandContext.FromCli(cli, TextWriter.Null, TextWriter.Null);

        Assert.Equal("git", ctx.CommandName);
        Assert.Equal(["-C", "/path", "status"], ctx.Operands);
        Assert.Equal(["git", "-C", "/path", "status"], ctx.OriginalCommandArgs);
    }

    [Fact]
    public void FromCli_git_dash_lowercase_c_key_value_flag_stays_in_operands_and_order()
    {
        var cli = new CliOptions(Raw: false, DetailLevel: DetailLevel.Default,
            CommandArgs: ["git", "-c", "user.name=Test", "log"]);
        var ctx = CommandContext.FromCli(cli, TextWriter.Null, TextWriter.Null);

        Assert.Equal("git", ctx.CommandName);
        Assert.Equal(["-c", "user.name=Test", "log"], ctx.Operands);
        Assert.Equal(["git", "-c", "user.name=Test", "log"], ctx.OriginalCommandArgs);
    }

    [Fact]
    public void FromCli_preserves_quoted_operand_as_a_single_element()
    {
        // The shell/CLI parser has already resolved quoting by the time args[] reaches us —
        // an operand like `-m "my message"` arrives as one array element with the space intact.
        var cli = new CliOptions(Raw: false, DetailLevel: DetailLevel.Default,
            CommandArgs: ["git", "commit", "-m", "my message with spaces"]);
        var ctx = CommandContext.FromCli(cli, TextWriter.Null, TextWriter.Null);

        Assert.Equal(["commit", "-m", "my message with spaces"], ctx.Operands);
        Assert.Equal(["git", "commit", "-m", "my message with spaces"], ctx.OriginalCommandArgs);
    }

    [Fact]
    public void FromCli_empty_input_yields_empty_command_name_and_operands()
    {
        var cli = CliOptionsParser.Parse([]);
        var ctx = CommandContext.FromCli(cli, TextWriter.Null, TextWriter.Null);

        Assert.Equal("", ctx.CommandName);
        Assert.Empty(ctx.Operands);
        Assert.Empty(ctx.OriginalCommandArgs);
    }
}
