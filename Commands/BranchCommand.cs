using System.Text;
using Tk.Common;
using Tk.Filters;

namespace Tk.Commands;

public sealed class BranchCommand : ICommand
{
    public string Name => "branch";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        var current = await ResolveCurrentBranchAsync(ctx.Process);
        if (current is null)
        {
            ctx.Err.Write("tk branch: not a git repository\n");
            return 1;
        }

        var explicitBase = ctx.Args.FirstOrDefault(a => !a.StartsWith('-'));
        var baseRef = explicitBase ?? await ResolveBaseAsync(ctx.Process, current);
        if (baseRef is null)
        {
            ctx.Err.Write($"tk branch: cannot resolve base for {current} (try: tk branch <ref>)\n");
            return 1;
        }

        var range = $"{baseRef}...{current}";

        var (countsExit, countsStdout, countsStderr) =
            await ctx.Process.RunAsync(["git", "rev-list", "--left-right", "--count", range]);
        if (countsExit != 0)
        {
            ctx.Err.Write(ProcessOutput.Combine(countsStdout, countsStderr));
            return countsExit;
        }
        var (behind, ahead) = ParseLeftRight(countsStdout);

        var sb = new StringBuilder();
        sb.AppendLine($"branch {current} base={baseRef} ahead={ahead} behind={behind}");

        if (ahead == 0)
        {
            ctx.Out.Write(sb.ToString());
            return 0;
        }

        var commitsLimit = ctx.DetailLevel == DetailLevel.More ? 30 : 10;
        var (logExit, logStdout, logStderr) = await ctx.Process.RunAsync(
            ["git", "log", "--oneline", "--no-decorate", $"-{commitsLimit + 1}", $"{baseRef}..{current}"]);
        if (logExit != 0)
        {
            ctx.Err.Write(ProcessOutput.Combine(logStdout, logStderr));
            return logExit;
        }

        var commits = logStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (commits.Length > 0)
        {
            sb.AppendLine("commits:");
            foreach (var commit in commits.Take(commitsLimit))
                sb.AppendLine($"  {commit.TrimEnd('\r')}");
            if (commits.Length > commitsLimit)
                sb.AppendLine(Ansi.Dim($"  +{commits.Length - commitsLimit} more"));
        }

        var (diffExit, diffStdout, diffStderr) = await ctx.Process.RunAsync(
            ["git", "diff", $"{baseRef}...{current}"]);
        if (diffExit == 0)
        {
            var diffRaw = ProcessOutput.Combine(diffStdout, diffStderr);
            if (ctx.Raw)
                sb.Append(diffRaw);
            else
                sb.Append(new GitDiffFilter(ctx.DetailLevel).Apply(diffRaw, 0));
        }

        ctx.Out.Write(sb.ToString());
        return 0;
    }

    private static async Task<string?> ResolveCurrentBranchAsync(IProcessRunner runner)
    {
        var (exit, stdout, _) = await runner.RunAsync(["git", "rev-parse", "--abbrev-ref", "HEAD"]);
        if (exit != 0)
            return null;
        var name = stdout.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static async Task<string?> ResolveBaseAsync(IProcessRunner runner, string current)
    {
        var upstream = await TryRevParseAsync(runner, $"{current}@{{upstream}}");
        if (upstream is not null)
            return upstream;

        foreach (var candidate in new[] { "origin/main", "origin/master", "main", "master" })
        {
            if (candidate == current)
                continue;
            if (await TryRevParseAsync(runner, candidate) is not null)
                return candidate;
        }

        return null;
    }

    private static async Task<string?> TryRevParseAsync(IProcessRunner runner, string refName)
    {
        var (exit, _, _) = await runner.RunAsync(["git", "rev-parse", "--verify", "--quiet", refName]);
        return exit == 0 ? refName : null;
    }

    private static (int Behind, int Ahead) ParseLeftRight(string stdout)
    {
        var parts = stdout.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return (0, 0);
        _ = int.TryParse(parts[0], out var behind);
        _ = int.TryParse(parts[1], out var ahead);
        return (behind, ahead);
    }
}
