using System.Text;
using System.Text.RegularExpressions;

namespace Tk.Filters;

public sealed partial class DotnetTestFilter : IOutputFilter
{
    public string Apply(string raw, int exitCode)
    {
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

            // Stack trace / error details for current failure (indented lines)
            if (currentFailure != null && line.StartsWith("    ") && !string.IsNullOrWhiteSpace(line))
            {
                if (currentFailure.Details.Count < 5) // Limit detail lines
                    currentFailure.Details.Add(line.Trim());
                continue;
            }

            // Empty line ends current failure context
            if (currentFailure != null && string.IsNullOrWhiteSpace(line))
                currentFailure = null;

            // Duration
            var durMatch = DurationRe().Match(line);
            if (durMatch.Success)
                duration = durMatch.Groups[1].Value;
        }

        var sb = new StringBuilder();
        var hasFailures = failed > 0 || failedTests.Count > 0;
        var status = hasFailures ? Ansi.Red("FAIL") : Ansi.Green("ok");

        if (total == 0 && failedTests.Count == 0)
        {
            // No test results found
            sb.Append($"{status} dotnet test: completed");
        }
        else if (hasFailures)
        {
            sb.Append($"{status} dotnet test: {passed} passed, {failed} failed, {skipped} skipped");
        }
        else
        {
            sb.Append($"ok dotnet test: {passed} passed");
            if (skipped > 0) sb.Append($", {skipped} skipped");
        }

        if (projectCount > 0)
            sb.Append($" in {projectCount} projects");
        if (duration != null)
            sb.Append($" ({duration})");
        sb.AppendLine();

        // Show failed tests
        if (failedTests.Count > 0)
        {
            sb.AppendLine(Ansi.Dim("---"));
            sb.AppendLine(Ansi.Red("Failed:"));
            foreach (var ft in failedTests.Take(15))
            {
                sb.AppendLine($"  {ft.Name}");
                foreach (var detail in ft.Details)
                    sb.AppendLine($"    {Truncate(detail, 200)}");
            }
            if (failedTests.Count > 15)
                sb.AppendLine($"  ... +{failedTests.Count - 15} more");
        }

        return sb.ToString();
    }

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "..." : s;

    [GeneratedRegex(@"(?:Passed|Failed)!\s+-\s+Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+),\s*Total:\s*(?<total>\d+)")]
    private static partial Regex SummaryRe();

    [GeneratedRegex(@"^\s+Failed\s+(.+?)(?:\s+\[\S+\])?\s*$")]
    private static partial Regex FailedTestRe();

    [GeneratedRegex(@"Time Elapsed\s+(\S+)")]
    private static partial Regex DurationRe();

    private class FailedTest(string name)
    {
        public string Name { get; } = name;
        public List<string> Details { get; } = [];
    }
}
