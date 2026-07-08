using System.Text;
using Tk.Common;
using Tk.Filters;

namespace Tk.Commands;

public sealed class ChangesCommand : ICommand
{
    public string Name => "changes";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        // Plain `git status` probes for in-progress repo state (rebase/merge/conflict/etc.),
        // the same way `tk git status` does — otherwise this card silently omits it.
        string? stateRaw = null;
        if (!ctx.Raw)
        {
            var (stateExitCode, stateStdout, stateStderr) = await ctx.Process.RunAsync(["git", "status"]);
            stateRaw = ProcessOutput.Combine(stateStdout, stateStderr);
            if (stateExitCode != 0)
            {
                ctx.Out.Write(stateRaw);
                return stateExitCode;
            }
        }

        var (statusExitCode, statusStdout, statusStderr) = await ctx.Process.RunAsync(
            ["git", "status", "--porcelain=v1", "--branch"]);
        var statusRaw = ProcessOutput.Combine(statusStdout, statusStderr);
        if (statusExitCode != 0)
        {
            ctx.Out.Write(statusRaw);
            return statusExitCode;
        }

        var (diffExitCode, diffStdout, diffStderr) = await ctx.Process.RunAsync(["git", "diff"]);
        var diffRaw = ProcessOutput.Combine(diffStdout, diffStderr);
        if (diffExitCode != 0)
        {
            ctx.Out.Write(diffRaw);
            return diffExitCode;
        }

        if (ctx.Raw)
        {
            var rawOutput = new StringBuilder();
            rawOutput.Append(statusRaw.TrimEnd());
            if (!string.IsNullOrWhiteSpace(diffRaw))
            {
                rawOutput.AppendLine();
                rawOutput.Append(diffRaw.TrimEnd());
            }
            rawOutput.AppendLine();
            ctx.Out.Write(rawOutput.ToString());
            return 0;
        }

        ctx.RawCharCount = statusRaw.Length + diffRaw.Length;
        ctx.RawLineCount = HiddenLinesFooter.CountLines(statusRaw) + HiddenLinesFooter.CountLines(diffRaw);

        var statusOutput = new GitStatusFilter(ctx.DetailLevel).Apply(statusRaw, 0, stateRaw).TrimEnd();
        var diffOutput = new GitDiffFilter(ctx.DetailLevel).Apply(diffRaw, 0).TrimEnd();

        var sb = new StringBuilder();
        sb.AppendLine(statusOutput);
        if (!string.Equals(diffOutput, "ok diff f=0", StringComparison.Ordinal))
            sb.AppendLine(diffOutput);

        var originalLines = HiddenLinesFooter.CountLines(statusRaw) + HiddenLinesFooter.CountLines(diffRaw);
        var shownLines = HiddenLinesFooter.CountLines(sb.ToString());
        var footer = OutputFooter.Format(originalLines, shownLines, unparsedCount: 0, ctx.DetailLevel);
        if (footer is not null)
            sb.AppendLine(footer);

        ctx.Out.Write(sb.ToString());
        return 0;
    }

}
