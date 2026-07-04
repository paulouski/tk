using System.Text;
using System.Text.RegularExpressions;
using Tk.Common;

namespace Tk.Filters;

public sealed partial class DotnetTestFilter(DetailLevel detailLevel = DetailLevel.Default) : IOutputFilter
{
    private const int MessageKeepLines = 12;
    private const int MessageCapThreshold = 15;

    public string Apply(string raw, int exitCode)
    {
        var more = detailLevel == DetailLevel.More;
        var lines = raw.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var failedTests = new List<FailedTest>();
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var total = 0;
        string? duration = null;
        int projectCount = 0;
        FailedTest? currentFailure = null;

        foreach (var line in lines)
        {
            // Summary line: "Passed!  - Failed: 0, Passed: 10, Skipped: 2, Total: 12"
            // or "Failed!  - Failed: 1, Passed: 9, Skipped: 2, Total: 12"
            var summaryMatch = SummaryRe().Match(line);
            if (summaryMatch.Success)
            {
                projectCount++;
                if (int.TryParse(summaryMatch.Groups["passed"].Value, out var p)) passed += p;
                if (int.TryParse(summaryMatch.Groups["failed"].Value, out var f)) failed += f;
                if (int.TryParse(summaryMatch.Groups["skipped"].Value, out var s)) skipped += s;
                if (int.TryParse(summaryMatch.Groups["total"].Value, out var t)) total += t;
                currentFailure = null;
                continue;
            }

            // Failed test line: "  Failed TestName [duration]"
            var failedMatch = FailedTestRe().Match(line);
            if (failedMatch.Success)
            {
                currentFailure = new FailedTest(failedMatch.Groups[1].Value.Trim());
                failedTests.Add(currentFailure);
                continue;
            }

            if (currentFailure != null)
            {
                // "Error Message:" header — content starts in message mode by default already,
                // this just marks the boundary explicitly (nothing to capture from the header itself).
                if (ErrorMessageHeaderRe().IsMatch(line))
                    continue;

                // "Stack Trace:" header switches capture from the assertion message to stack frames.
                if (StackTraceHeaderRe().IsMatch(line))
                {
                    currentFailure.InStackSection = true;
                    continue;
                }

                // Empty line ends current failure context
                if (string.IsNullOrWhiteSpace(line))
                {
                    currentFailure = null;
                    continue;
                }

                if (currentFailure.InStackSection)
                    currentFailure.Stack.Add(line.Trim());
                else
                    currentFailure.Message.Add(line.Trim());
                continue;
            }

            // Duration
            var durMatch = DurationRe().Match(line);
            if (durMatch.Success)
                duration = durMatch.Groups[1].Value;
        }

        var sb = new StringBuilder();
        var commandFailed = exitCode != 0;
        var hasFailures = commandFailed || failed > 0 || failedTests.Count > 0;
        var status = hasFailures ? Ansi.Red("FAIL") : Ansi.Green("ok");

        if (commandFailed && total == 0 && failedTests.Count == 0)
        {
            sb.Append($"{status} test e=1");
        }
        else if (total == 0 && failedTests.Count == 0)
        {
            sb.Append($"{status} test");
        }
        else if (hasFailures)
        {
            sb.Append($"{status} test pass={passed} fail={failed}");
            if (skipped > 0) sb.Append($" skip={skipped}");
        }
        else
        {
            sb.Append($"ok test pass={passed}");
            if (skipped > 0) sb.Append($" skip={skipped}");
        }

        if (projectCount > 0)
            sb.Append($" p={projectCount}");
        if (duration != null)
            sb.Append($" t={duration}");
        sb.AppendLine();

        if (commandFailed && failedTests.Count == 0)
            AppendRawTail(sb, lines, 12);

        // Show failed tests — assertion message (the single most useful line) plus stack
        // context for every failed test. Default: message + first stack frame. --more: full stack.
        if (failedTests.Count > 0)
        {
            sb.AppendLine(Ansi.Dim("---"));
            sb.AppendLine(Ansi.Red("Failed:"));
            foreach (var ft in failedTests)
            {
                sb.AppendLine($"  {ft.Name}");
                AppendMessage(sb, ft.Message);

                var framesToShow = more ? ft.Stack.Count : Math.Min(1, ft.Stack.Count);
                foreach (var frame in ft.Stack.Take(framesToShow))
                    sb.AppendLine($"    {frame}");
            }
        }

        return sb.ToString();
    }

    private static void AppendMessage(StringBuilder sb, List<string> message)
    {
        if (message.Count == 0)
            return;

        if (message.Count > MessageCapThreshold)
        {
            foreach (var line in message.Take(MessageKeepLines))
                sb.AppendLine($"    {line}");
            sb.AppendLine("    …");
            return;
        }

        foreach (var line in message)
            sb.AppendLine($"    {line}");
    }

    [GeneratedRegex(@"(?:Passed|Failed)!\s+-\s+Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+),\s*Total:\s*(?<total>\d+)")]
    private static partial Regex SummaryRe();

    [GeneratedRegex(@"^\s+Failed\s+(.+?)(?:\s+\[\S+\])?\s*$")]
    private static partial Regex FailedTestRe();

    [GeneratedRegex(@"Time Elapsed\s+(\S+)")]
    private static partial Regex DurationRe();

    [GeneratedRegex(@"^\s*Error Message:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorMessageHeaderRe();

    [GeneratedRegex(@"^\s*Stack Trace:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex StackTraceHeaderRe();

    private static void AppendRawTail(StringBuilder sb, string[] lines, int maxLines)
    {
        var tail = lines
            .Select(l => l.TrimEnd())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .TakeLast(maxLines)
            .ToArray();

        if (tail.Length == 0)
            return;

        sb.AppendLine(Ansi.Dim("--- raw tail ---"));
        foreach (var line in tail)
            sb.AppendLine($"  {line}");
    }

    private class FailedTest(string name)
    {
        public string Name { get; } = name;
        public List<string> Message { get; } = [];
        public List<string> Stack { get; } = [];
        public bool InStackSection { get; set; }
    }
}
