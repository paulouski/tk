namespace Tk.Commands;

/// <summary>
/// Per-invocation context for a built-in command. Holds args (without the command name),
/// global flags resolved by <see cref="CliOptionsParser"/>, writers for stdout/stderr,
/// and the <see cref="IProcessRunner"/> used to shell out to git/dotnet/rg.
/// </summary>
public sealed class CommandContext
{
    public string[] Args { get; }
    public DetailLevel DetailLevel { get; }
    public bool Raw { get; }
    public TextWriter Out { get; }
    public TextWriter Err { get; }
    public IProcessRunner Process { get; }

    // Settable by compacting commands to report pre-compaction size for analytics.
    public long? RawCharCount { get; set; }
    public int? RawLineCount { get; set; }

    public CommandContext(
        string[] args,
        DetailLevel detailLevel,
        bool raw,
        TextWriter @out,
        TextWriter err,
        IProcessRunner process)
    {
        Args = args;
        DetailLevel = detailLevel;
        Raw = raw;
        Out = @out;
        Err = err;
        Process = process;
    }

    /// <summary>Builds a context from the parsed <see cref="CliOptions"/>, stripping the command name.</summary>
    public static CommandContext FromCli(
        CliOptions cli,
        TextWriter @out,
        TextWriter err,
        IProcessRunner? process = null)
    {
        var args = cli.CommandArgs.Length > 0 ? cli.CommandArgs[1..] : [];
        return new CommandContext(args, cli.DetailLevel, cli.Raw, @out, err, process ?? ProcessRunner.Default);
    }
}
