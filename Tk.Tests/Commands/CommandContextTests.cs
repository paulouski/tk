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
}
