namespace Tk.Commands;

/// <summary>
/// Per-invocation context for a built-in command. Holds the command name and its operands
/// (tk's own global flags already extracted by <see cref="CliOptionsParser"/>), writers for
/// stdout/stderr, and the <see cref="IProcessRunner"/> used to shell out to git/dotnet/rg.
/// </summary>
public sealed class CommandContext
{
    /// <summary>
    /// The leading command token as the user typed it (e.g. "git", "dotnet", "view"). tk's own
    /// global flags (--more/--raw), if they appeared before it, have already been extracted and
    /// are exposed separately via <see cref="DetailLevel"/>/<see cref="Raw"/>. Empty string when
    /// no command name applies (e.g. a context built directly by a test without one).
    /// </summary>
    public string CommandName { get; }

    /// <summary>
    /// Everything after <see cref="CommandName"/>: the command's own operands and flags. This is
    /// what a built-in command should inspect to find subcommands, paths, etc.
    /// </summary>
    public string[] Operands { get; }

    /// <summary>Alias for <see cref="Operands"/>, kept so existing call sites read unchanged.</summary>
    public string[] Args => Operands;

    /// <summary>
    /// <see cref="CommandName"/> followed by <see cref="Operands"/> — the full command line as the
    /// user typed it, minus tk's own global flags. Pass this (never <see cref="Operands"/> alone)
    /// when spawning a process for the wrapped command, so the binary name is never dropped.
    /// </summary>
    public string[] OriginalCommandArgs => CommandName.Length == 0 ? Operands : [CommandName, .. Operands];

    public DetailLevel DetailLevel { get; }
    public bool Raw { get; }
    public TextWriter Out { get; }
    public TextWriter Err { get; }
    public IProcessRunner Process { get; }

    // Settable by compacting commands to report pre-compaction size for analytics.
    public long? RawCharCount { get; set; }
    public int? RawLineCount { get; set; }
    // Settable by search/list commands to report the result count for empty-detection analytics.
    public int? ResultCount { get; set; }

    public CommandContext(
        string[] args,
        DetailLevel detailLevel,
        bool raw,
        TextWriter @out,
        TextWriter err,
        IProcessRunner process,
        string commandName = "")
    {
        Operands = args;
        CommandName = commandName;
        DetailLevel = detailLevel;
        Raw = raw;
        Out = @out;
        Err = err;
        Process = process;
    }

    /// <summary>
    /// Builds a context from the parsed <see cref="CliOptions"/>: the leading token becomes
    /// <see cref="CommandName"/>, everything after it becomes <see cref="Operands"/>.
    /// </summary>
    public static CommandContext FromCli(
        CliOptions cli,
        TextWriter @out,
        TextWriter err,
        IProcessRunner? process = null)
    {
        var commandName = cli.CommandArgs.Length > 0 ? cli.CommandArgs[0] : "";
        var operands = cli.CommandArgs.Length > 0 ? cli.CommandArgs[1..] : [];
        return new CommandContext(operands, cli.DetailLevel, cli.Raw, @out, err, process ?? ProcessRunner.Default, commandName);
    }
}
