using Tk.Common;
using Tk.Filters;

namespace Tk.Commands;

public sealed class GitCommand : ICommand
{
    public string Name => "git";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        if (ctx.Raw)
            return await RunFilteredAsync(ctx.Args, new PassthroughFilter(), ctx);

        var subcommand = FindSubcommand(ctx.Args);
        return subcommand?.Value.ToLowerInvariant() switch
        {
            "status" => await RunStatusAsync(ctx, subcommand.Value.Index),
            "diff" => await RunDiffAsync(ctx, subcommand.Value.Index, show: false),
            "show" => await RunDiffAsync(ctx, subcommand.Value.Index, show: true),
            "log" => await RunLogAsync(ctx, subcommand.Value.Index),
            "add" or "commit" or "push" or "pull" or "fetch"
                or "stash" or "branch" or "checkout" or "switch"
                or "merge" or "rebase" or "reset" or "tag" =>
                await RunFilteredAsync(ctx.Args, new GitCompactFilter(), ctx),
            _ => await RunFilteredAsync(ctx.Args, new PassthroughFilter(), ctx)
        };
    }

    private static async Task<int> RunStatusAsync(CommandContext ctx, int subcommandIndex)
    {
        var userArgs = ctx.Args[(subcommandIndex + 1)..];
        if (userArgs.Length > 0)
            return await RunFilteredAsync(ctx.Args, new GitStatusFilter(ctx.DetailLevel), ctx);

        var globalArgs = ctx.Args[..subcommandIndex];
        var plainArgs = BuildGitArgs(globalArgs, ["status"]);
        var (plainExit, plainStdout, plainStderr) = await ctx.Process.RunAsync(plainArgs);
        var plainRaw = ProcessOutput.Combine(plainStdout, plainStderr);
        if (plainExit != 0)
        {
            ctx.Out.Write(plainRaw);
            return plainExit;
        }

        var porcelainArgs = BuildGitArgs(globalArgs, ["status", "--porcelain=v1", "--branch"]);
        var (exitCode, stdout, stderr) = await ctx.Process.RunAsync(porcelainArgs);
        var raw = ProcessOutput.Combine(stdout, stderr);
        var filtered = new GitStatusFilter(ctx.DetailLevel).Apply(raw, exitCode, plainRaw);
        ctx.Out.Write(filtered);
        return exitCode;
    }

    private static async Task<int> RunDiffAsync(CommandContext ctx, int subcommandIndex, bool show)
    {
        var subcommand = ctx.Args[subcommandIndex];
        var globalArgs = ctx.Args[..subcommandIndex];
        var userArgs = ctx.Args[(subcommandIndex + 1)..];
        var passthrough = userArgs.Any(IsDiffPassthroughFlag)
            || show && userArgs.Any(IsBlobShowArg);
        var effectiveUserArgs = userArgs.Where(a => a != "--no-compact").ToArray();
        var normalized = show ? effectiveUserArgs : NormalizeDiffArgs(effectiveUserArgs);
        var args = BuildGitArgs(globalArgs, [subcommand, .. normalized]);
        return await RunFilteredAsync(args, passthrough ? new PassthroughFilter() : new GitDiffFilter(ctx.DetailLevel), ctx);
    }

    private static async Task<int> RunLogAsync(CommandContext ctx, int subcommandIndex)
    {
        var globalArgs = ctx.Args[..subcommandIndex];
        var userArgs = ctx.Args[(subcommandIndex + 1)..];
        var effective = new List<string> { "log" };
        var hasFormat = userArgs.Any(a => a == "--oneline" || a.StartsWith("--pretty", StringComparison.Ordinal) || a.StartsWith("--format", StringComparison.Ordinal));
        var hasLimit = HasUserLimit(userArgs);
        var wantsMerges = userArgs.Any(a => a == "--merges" || a == "--min-parents=2");

        if (!hasFormat)
            effective.Add("--pretty=format:%h %s (%ar) <%an>%n%b%n---END---");
        if (!hasLimit)
            effective.Add("-10");
        if (!wantsMerges && !hasFormat && !hasLimit)
            effective.Add("--no-merges");
        effective.AddRange(userArgs);

        var args = BuildGitArgs(globalArgs, effective);
        return await RunFilteredAsync(args, new GitLogFilter(), ctx);
    }

    private static async Task<int> RunFilteredAsync(string[] args, IOutputFilter filter, CommandContext ctx)
    {
        var (exitCode, stdout, stderr) = await ctx.Process.RunAsync(args);
        var raw = ProcessOutput.Combine(stdout, stderr);
        var filtered = filter.Apply(raw, exitCode);
        if (exitCode != 0)
            filtered = RawOutputStore.AppendFailureReference(raw, filtered, ["git", .. ctx.Args]);
        ctx.Out.Write(filtered);
        return exitCode;
    }

    private static string[] BuildGitArgs(IEnumerable<string> globalArgs, IEnumerable<string> args) =>
        ["git", .. globalArgs, .. args];

    private static (string Value, int Index)? FindSubcommand(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith('-'))
                return (arg, i);
            if (GitOptionNeedsValue(arg) && !arg.Contains('='))
                i++;
        }

        return null;
    }

    private static bool GitOptionNeedsValue(string arg) => arg is
        "-c" or "-C" or "--git-dir" or "--work-tree" or "--namespace"
        or "--super-prefix" or "--config-env";

    private static bool IsDiffPassthroughFlag(string arg) =>
        arg is "--stat" or "--numstat" or "--shortstat" or "--no-compact";

    private static bool IsBlobShowArg(string arg) =>
        !arg.StartsWith('-') && arg.Contains(':', StringComparison.Ordinal);

    private static string[] NormalizeDiffArgs(string[] args)
    {
        if (args.Any(a => a == "--"))
            return args;

        var pathStart = Array.FindIndex(args, IsLikelyPathArg);
        if (pathStart < 0)
            return args;

        return [.. args[..pathStart], "--", .. args[pathStart..]];
    }

    private static bool IsLikelyPathArg(string arg)
    {
        if (arg.StartsWith('-'))
            return false;
        if (arg.StartsWith('.') || arg.StartsWith('~'))
            return true;
        return (arg.Contains('/') || arg.Contains('\\')) && (File.Exists(arg) || Directory.Exists(arg));
    }

    private static bool HasUserLimit(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Length > 1 && arg[0] == '-' && char.IsDigit(arg[1]))
                return true;
            if (arg == "-n" || arg == "--max-count")
                return i + 1 < args.Length;
            if (arg.StartsWith("--max-count=", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
