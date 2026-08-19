using Tk.Common;
using Tk.Filters;

namespace Tk.Commands;

public sealed class GitCommand : ICommand
{
    public string Name => "git";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        // ctx.OriginalCommandArgs carries "git" plus the user's operands, so the spawned
        // process is always the real `git` binary regardless of the subcommand name.
        if (ctx.Raw)
            return await RunFilteredAsync(ctx.OriginalCommandArgs, new PassthroughFilter(), ctx);

        var subcommand = FindSubcommand(ctx.Args);
        return subcommand?.Value.ToLowerInvariant() switch
        {
            "status" => await RunStatusAsync(ctx, subcommand.Value.Index),
            "diff" => await RunDiffAsync(ctx, subcommand.Value.Index, show: false),
            "show" => await RunDiffAsync(ctx, subcommand.Value.Index, show: true),
            "log" => await RunLogAsync(ctx, subcommand.Value.Index),
            // Everything else (add, commit, push, pull, fetch, stash, branch, checkout,
            // merge, rebase, reset, tag, ...) is a clean, unfiltered passthrough to real git.
            _ => await RunFilteredAsync(ctx.OriginalCommandArgs, new PassthroughFilter(), ctx)
        };
    }

    private static async Task<int> RunStatusAsync(CommandContext ctx, int subcommandIndex)
    {
        var userArgs = ctx.Args[(subcommandIndex + 1)..];
        if (userArgs.Any(IsPorcelainFlag))
            return await RunFilteredAsync(ctx.OriginalCommandArgs, new PassthroughFilter(), ctx);
        if (userArgs.Length > 0)
            return await RunFilteredAsync(ctx.OriginalCommandArgs, new GitStatusFilter(ctx.DetailLevel), ctx);

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
        ctx.RawCharCount = raw.Length;
        ctx.RawLineCount = HiddenLinesFooter.CountLines(raw);
        var ledger = new UnitLedger();
        var filtered = new GitStatusFilter(ctx.DetailLevel).Apply(raw, exitCode, plainRaw, ledger);
        if (!ctx.Raw)
            filtered = OutputPipeline.AppendFooter(raw, filtered, ctx.DetailLevel, ledger, exitCode, ctx.OriginalCommandArgs);
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
        var summary = userArgs.Any(a => a == "--summary");
        var effectiveUserArgs = userArgs.Where(a => a != "--no-compact" && a != "--summary").ToArray();
        var normalized = show ? effectiveUserArgs : NormalizeDiffArgs(effectiveUserArgs);
        var args = BuildGitArgs(globalArgs, [subcommand, .. normalized]);
        return await RunFilteredAsync(args, passthrough ? new PassthroughFilter() : new GitDiffFilter(ctx.DetailLevel, summary: summary, isShow: show), ctx);
    }

    // The cap tk injects into `git log` when the user gave no limit of their own.
    private const int LogDisplayCap = 10;

    private static async Task<int> RunLogAsync(CommandContext ctx, int subcommandIndex)
    {
        var globalArgs = ctx.Args[..subcommandIndex];
        var userArgs = ctx.Args[(subcommandIndex + 1)..];
        var effective = new List<string> { "log" };
        var hasFormat = userArgs.Any(a => a == "--oneline" || a.StartsWith("--pretty", StringComparison.Ordinal) || a.StartsWith("--format", StringComparison.Ordinal));
        var hasLimit = HasUserLimit(userArgs);
        var wantsMerges = userArgs.Any(a => a == "--merges" || a == "--min-parents=2");
        var addedNoMerges = !wantsMerges && !hasFormat && !hasLimit;

        if (!hasFormat)
            effective.Add("--pretty=format:%h %s (%ar) <%an>");
        if (!hasLimit)
            effective.Add($"-{LogDisplayCap}");
        if (addedNoMerges)
            effective.Add("--no-merges");
        effective.AddRange(userArgs);

        var args = BuildGitArgs(globalArgs, effective);

        // tk injected the display cap above (no user limit) — figure out, cheaply, whether the
        // real history is longer than what we're about to show, so the footer can say so instead
        // of silently truncating. Never fetches the whole history: just an exact `rev-list --count`
        // with the same effective filters (no format, no injected cap). Any failure here falls
        // back to no signal — it must never break `git log` itself.
        var extraHidden = hasLimit
            ? 0
            : await CountHiddenBeyondCapAsync(ctx, globalArgs, userArgs, addedNoMerges);

        return await OutputPipeline.RunAsync(args, new GitLogFilter(), ctx, extraHiddenCount: extraHidden);
    }

    private static async Task<int> CountHiddenBeyondCapAsync(
        CommandContext ctx, string[] globalArgs, string[] userArgs, bool noMerges)
    {
        try
        {
            var countArgs = new List<string> { "rev-list", "--count" };
            if (noMerges)
                countArgs.Add("--no-merges");
            if (!HasRevisionArg(userArgs))
                countArgs.Add("HEAD");
            countArgs.AddRange(userArgs);

            var (exitCode, stdout, _) = await ctx.Process.RunAsync(BuildGitArgs(globalArgs, countArgs));
            if (exitCode != 0 || !int.TryParse(stdout.Trim(), out var total))
                return 0;

            return total > LogDisplayCap ? total - LogDisplayCap : 0;
        }
        catch
        {
            return 0;
        }
    }

    // Whether userArgs already pins a revision (branch, ref, range) ahead of any `--` path
    // separator — if so, `git rev-list --count` doesn't need an explicit `HEAD` appended.
    private static bool HasRevisionArg(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg == "--")
                break;
            if (arg.Length > 0 && arg[0] != '-')
                return true;
        }

        return false;
    }

    private static Task<int> RunFilteredAsync(string[] args, IOutputFilter filter, CommandContext ctx)
        => OutputPipeline.RunAsync(args, filter, ctx);

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

    private static bool IsPorcelainFlag(string arg) =>
        arg == "--porcelain" || arg.StartsWith("--porcelain=", StringComparison.Ordinal) || arg == "-z";

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
