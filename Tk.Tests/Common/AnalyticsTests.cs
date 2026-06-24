using Tk.Common;
using Xunit;

namespace Tk.Tests.Common;

public class AnalyticsTests
{
    // --- Classify tests ---

    [Fact]
    public void Classify_simple_command_returns_command()
    {
        var (cmd, sub, flags, operands) = Analytics.Classify(["focus", "wallet balance", "src/"]);
        Assert.Equal("focus", cmd);
        Assert.Null(sub);
        Assert.Empty(flags);
        Assert.Equal(2, operands);
    }

    [Fact]
    public void Classify_git_captures_subcommand()
    {
        var (cmd, sub, flags, operands) = Analytics.Classify(["git", "status", "--short"]);
        Assert.Equal("git", cmd);
        Assert.Equal("status", sub);
        Assert.Equal(["--short"], flags);
        Assert.Equal(0, operands);
    }

    [Fact]
    public void Classify_dotnet_captures_subcommand()
    {
        var (cmd, sub, flags, operands) = Analytics.Classify(["dotnet", "build", "--no-restore"]);
        Assert.Equal("dotnet", cmd);
        Assert.Equal("build", sub);
        Assert.Equal(["--no-restore"], flags);
        Assert.Equal(0, operands);
    }

    [Fact]
    public void Classify_non_flag_args_counted_not_stored()
    {
        var (cmd, sub, flags, operands) = Analytics.Classify(["focus", "query", "path/to/dir", "extra"]);
        Assert.Equal("focus", cmd);
        Assert.Null(sub);           // focus is not git/dotnet
        Assert.Empty(flags);
        Assert.Equal(3, operands);  // all three non-flag args counted
    }

    [Fact]
    public void Classify_git_extra_operands_counted()
    {
        // git log <branch> — sub=log, then "main" is a further operand
        var (cmd, sub, flags, operands) = Analytics.Classify(["git", "log", "main"]);
        Assert.Equal("git", cmd);
        Assert.Equal("log", sub);
        Assert.Empty(flags);
        Assert.Equal(1, operands);
    }

    [Fact]
    public void Classify_flags_collected()
    {
        var (_, _, flags, _) = Analytics.Classify(["focus", "--files-only", "--more"]);
        Assert.Equal(["--files-only", "--more"], flags);
    }

    [Fact]
    public void Classify_empty_args_returns_empty()
    {
        var (cmd, sub, flags, operands) = Analytics.Classify([]);
        Assert.Equal(string.Empty, cmd);
        Assert.Null(sub);
        Assert.Empty(flags);
        Assert.Equal(0, operands);
    }

    [Fact]
    public void Classify_command_only_no_sub_no_flags()
    {
        var (cmd, sub, flags, operands) = Analytics.Classify(["tree"]);
        Assert.Equal("tree", cmd);
        Assert.Null(sub);
        Assert.Empty(flags);
        Assert.Equal(0, operands);
    }

    // --- Opt-out test ---

    [Fact]
    public void Record_with_opt_out_env_does_not_throw()
    {
        Environment.SetEnvironmentVariable("TK_ANALYTICS", "0");
        try
        {
            // Should silently return — no exception, no file written.
            Analytics.Record(["git", "status"], 0, "default", 10, 100, 5, null, null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TK_ANALYTICS", null);
        }
    }

    [Fact]
    public void Record_with_opt_out_false_does_not_throw()
    {
        Environment.SetEnvironmentVariable("TK_ANALYTICS", "false");
        try
        {
            Analytics.Record(["tree"], 0, "default", 5, 50, 3, 200L, 10);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TK_ANALYTICS", null);
        }
    }

    [Fact]
    public void Record_empty_args_does_not_throw()
    {
        // Guard: empty commandArgs should return silently.
        Analytics.Record([], 0, "default", 0, 0, 0, null, null);
    }
}
