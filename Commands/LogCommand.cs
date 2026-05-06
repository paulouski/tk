using Tk.Filters;

namespace Tk.Commands;

public sealed class LogCommand : ICommand
{
    public string Name => "log";

    public Task<int> RunAsync(CommandContext ctx)
    {
        if (ctx.Args.Length == 0 || ctx.Args[0].StartsWith('-'))
        {
            ctx.Err.WriteLine("tk log: file argument required");
            return Task.FromResult(1);
        }

        var filePath = ctx.Args[0];
        var flags = ctx.Args.Length > 1 ? ctx.Args[1..] : [];
        if (ctx.Raw && !flags.Contains("--all"))
            flags = [.. flags, "--all"];

        ctx.Out.Write(LogFileFilter.Apply(filePath, flags));
        return Task.FromResult(0);
    }
}
