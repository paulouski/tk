using Tk.Common;
using Tk.Filters;

namespace Tk.Commands;

public sealed class UnityCommand : ICommand
{
    public string Name => "unity";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        var sub = ctx.Args.Length > 0 ? ctx.Args[0] : null;
        var subCtx = SubContext(ctx);

        return sub?.ToLowerInvariant() switch
        {
            "tree" => await new TreeCommand().RunAsync(subCtx, unityMode: true),
            "files" => await new FilesCommand().RunAsync(subCtx, unityMode: true),
            "status" => await RunStatusAsync(ctx),
            _ => Usage(ctx)
        };
    }

    private static async Task<int> RunStatusAsync(CommandContext ctx)
    {
        var (plainExit, plainOut, plainErr) = await ctx.Process.RunAsync(["git", "status"]);
        var plainRaw = ProcessOutput.Combine(plainOut, plainErr);
        if (plainExit != 0)
        {
            ctx.Out.Write(plainRaw);
            return plainExit;
        }

        var (exit, out2, err2) = await ctx.Process.RunAsync(["git", "status", "--porcelain=v1", "--branch"]);
        var raw = ProcessOutput.Combine(out2, err2);
        var filtered = new GitStatusFilter(ctx.DetailLevel, unityMode: true).Apply(raw, exit, plainRaw);
        ctx.Out.Write(filtered);
        return exit;
    }

    private static int Usage(CommandContext ctx)
    {
        ctx.Out.WriteLine("unity: tree|files|status");
        return 0;
    }

    private static CommandContext SubContext(CommandContext ctx) =>
        new(
            ctx.Args.Length > 1 ? ctx.Args[1..] : [],
            ctx.DetailLevel, ctx.Raw, ctx.Out, ctx.Err, ctx.Process,
            commandName: ctx.Args.Length > 0 ? ctx.Args[0] : "");
}
