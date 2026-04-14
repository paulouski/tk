using System.Text;
using System.Text.RegularExpressions;

namespace Tk.Filters;

/// <summary>Compact dotnet restore output. Reuses NuGet dedup from DotnetBuildFilter via shared output.</summary>
public sealed partial class DotnetRestoreFilter : IOutputFilter
{
    public string Apply(string raw, int exitCode)
    {
        var lines = raw.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        int restoredCount = 0;
        var upToDate = false;
        string? duration = null;
        var nugetWarnings = new Dictionary<string, int>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Skip noise
            if (line.TrimStart().StartsWith("Determining projects") ||
                line.TrimStart().StartsWith("Nothing to do") ||
                line.Contains("Microsoft (R) Build Engine") ||
                line.Contains("Copyright (C) Microsoft"))
                continue;

            if (line.TrimStart().StartsWith("All projects are up-to-date"))
            {
                upToDate = true;
                continue;
            }

            // Restored line
            if (RestoredRe().IsMatch(line))
            {
                restoredCount++;
                continue;
            }

            // Duration
            var durMatch = DurationRe().Match(line);
            if (durMatch.Success)
            {
                duration = durMatch.Groups[1].Value;
                continue;
            }

            // NuGet vulnerability warnings
            if (line.Contains("severity vulnerability") || NugetWarnRe().IsMatch(line))
            {
                var normalized = NormalizeNugetWarning(line);
                nugetWarnings[normalized] = nugetWarnings.GetValueOrDefault(normalized) + 1;
                continue;
            }

            // Errors
            if (line.Contains(": error ") || ToolErrorRe().IsMatch(line))
            {
                errors.Add(line.Trim());
                continue;
            }
        }

        var sb = new StringBuilder();
        var status = exitCode == 0 ? Ansi.Green("ok") : Ansi.Red("FAIL");
        if (exitCode == 0 && errors.Count == 0 && nugetWarnings.Count == 0)
        {
            sb.Append(upToDate
                ? $"{status} restore up=1"
                : $"{status} restore p={restoredCount}");
        }
        else
        {
            sb.Append($"{status} restore p={restoredCount}");
            if (errors.Count > 0)
                sb.Append($" e={errors.Count}");
            if (nugetWarnings.Count > 0)
                sb.Append($" nu={nugetWarnings.Values.Sum()}");
        }
        if (duration != null)
            sb.Append($" t={duration}");
        sb.AppendLine();

        if (errors.Count > 0)
        {
            sb.AppendLine(Ansi.Red("Errors:"));
            foreach (var e in errors.Take(10))
                sb.AppendLine($"  {e}");
        }
        else if (exitCode != 0)
        {
            AppendRawTail(sb, lines, 12);
        }

        if (nugetWarnings.Count > 0)
        {
            sb.AppendLine(Ansi.Yellow("NuGet Vulnerabilities:"));
            foreach (var pair in nugetWarnings.Take(8))
            {
                var suffix = pair.Value > 1 ? $" (x{pair.Value})" : "";
                sb.AppendLine($"  {pair.Key}{suffix}");
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"Restored\s+")]
    private static partial Regex RestoredRe();

    [GeneratedRegex(@"Time Elapsed\s+(\S+)")]
    private static partial Regex DurationRe();

    [GeneratedRegex(@"warning NU\d{4}")]
    private static partial Regex NugetWarnRe();

    [GeneratedRegex(@"^.+?\s*:\s*error\s+[A-Za-z]*\d+\s*:")]
    private static partial Regex ToolErrorRe();

    private static string NormalizeNugetWarning(string line)
    {
        var trimmed = line.Trim();
        var marker = "[";
        var projectIndex = trimmed.LastIndexOf(marker, StringComparison.Ordinal);
        return projectIndex > 0 && trimmed.EndsWith(']') ? trimmed[..projectIndex].TrimEnd() : trimmed;
    }

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
}
