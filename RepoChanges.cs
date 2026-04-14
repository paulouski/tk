using System.Text;
using Tk.Filters;

namespace Tk;

public static class RepoChanges
{
    public static async Task<(int ExitCode, string Output)> RunAsync(CliOptions cliOptions)
    {
        var statusArgs = new[] { "git", "status", "--porcelain=v1", "--branch" };
        var (statusExitCode, statusStdout, statusStderr) = await CommandRunner.RunAsync(statusArgs);
        var statusRaw = Combine(statusStdout, statusStderr);
        if (statusExitCode != 0)
            return (statusExitCode, statusRaw);

        var diffArgs = new[] { "git", "diff" };
        var (diffExitCode, diffStdout, diffStderr) = await CommandRunner.RunAsync(diffArgs);
        var diffRaw = Combine(diffStdout, diffStderr);
        if (diffExitCode != 0)
            return (diffExitCode, diffRaw);

        if (cliOptions.Raw)
        {
            var rawOutput = new StringBuilder();
            rawOutput.Append(statusRaw.TrimEnd());
            if (!string.IsNullOrWhiteSpace(diffRaw))
            {
                rawOutput.AppendLine();
                rawOutput.Append(diffRaw.TrimEnd());
            }
            rawOutput.AppendLine();
            return (0, rawOutput.ToString());
        }

        var statusOutput = new GitStatusFilter(cliOptions.DetailLevel).Apply(statusRaw, 0).TrimEnd();
        var diffOutput = new GitDiffFilter(cliOptions.DetailLevel).Apply(diffRaw, 0).TrimEnd();

        var sb = new StringBuilder();
        sb.AppendLine(statusOutput);
        if (!string.Equals(diffOutput, "ok diff f=0", StringComparison.Ordinal))
            sb.AppendLine(diffOutput);

        return (0, sb.ToString());
    }

    private static string Combine(string stdout, string stderr) =>
        string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : $"{stdout.TrimEnd()}\n{stderr}";
}
