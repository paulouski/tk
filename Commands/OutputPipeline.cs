using Tk.Common;
using Tk.Filters;

namespace Tk.Commands;

/// <summary>
/// Canonical output pipeline shared by external-command dispatch (<see cref="Program"/>),
/// <see cref="GitCommand"/>, and any built-in that shells out and wants the same
/// footer/raw-store/ledger treatment. See docs/output-contract.md.
///
/// Steps: run process → combine stdout/stderr → filter with <see cref="UnitLedger"/> →
/// save raw copy when needed → append <see cref="OutputFooter"/> → write to ctx.Out.
/// </summary>
public static class OutputPipeline
{
    /// <summary>
    /// Runs <paramref name="args"/>, filters the combined output, appends the shared footer,
    /// and writes to <see cref="CommandContext.Out"/>. Records raw size on ctx for analytics.
    /// Pass <paramref name="rawStoreArgs"/> when the args used to spawn the process differ from
    /// the args that should identify the raw-copy (e.g. <c>tk grep</c> auto-<c>-r</c>); defaults
    /// to <see cref="CommandContext.OriginalCommandArgs"/>.
    /// </summary>
    public static async Task<int> RunAsync(
        string[] args,
        IOutputFilter filter,
        CommandContext ctx,
        string[]? rawStoreArgs = null)
    {
        var (exitCode, stdout, stderr) = await ctx.Process.RunAsync(args);
        var raw = ProcessOutput.Combine(stdout, stderr);
        ctx.RawCharCount = raw.Length;
        ctx.RawLineCount = HiddenLinesFooter.CountLines(raw);

        var ledger = new UnitLedger();
        var filtered = filter.Apply(raw, exitCode, ledger);

        if (!ctx.Raw)
            filtered = AppendFooter(raw, filtered, ctx.DetailLevel, ledger, exitCode, rawStoreArgs ?? ctx.OriginalCommandArgs);

        ctx.Out.Write(filtered);
        return exitCode;
    }

    /// <summary>
    /// Footer + raw-store step only, for callers that cannot use <see cref="RunAsync"/> because
    /// they run multiple processes or use a filter overload not on <see cref="IOutputFilter"/>
    /// (e.g. <c>GitStatusFilter.Apply(raw, exitCode, stateRaw, ledger)</c>). Shared so the footer
    /// format and raw-save policy stay in one place.
    /// </summary>
    public static string AppendFooter(
        string raw, string filtered, DetailLevel level, UnitLedger ledger,
        int exitCode, string[] commandArgs)
    {
        var rawPath = RawOutputStore.SaveIfNeeded(raw, filtered, commandArgs, exitCode, ledger.UnparsedCount);
        var footer = OutputFooter.Format(
            HiddenLinesFooter.CountLines(raw),
            HiddenLinesFooter.CountLines(filtered),
            ledger.UnparsedCount,
            level,
            rawPath);
        if (footer is null)
            return filtered;
        return filtered.EndsWith('\n') ? $"{filtered}{footer}\n" : $"{filtered}\n{footer}\n";
    }
}
