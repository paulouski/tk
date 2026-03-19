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
        string? duration = null;
        var nugetWarnings = new List<string>();
        var errors = new List<string>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Skip noise
            if (line.TrimStart().StartsWith("Determining projects") ||
                line.TrimStart().StartsWith("All projects are up-to-date") ||
                line.TrimStart().StartsWith("Nothing to do") ||
                line.Contains("Microsoft (R) Build Engine") ||
                line.Contains("Copyright (C) Microsoft"))
                continue;

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
                nugetWarnings.Add(line.Trim());
                continue;
            }

            // Errors
            if (line.Contains(": error "))
            {
                errors.Add(line.Trim());
                continue;
            }
        }

        var sb = new StringBuilder();
        var status = exitCode == 0 ? Ansi.Green("ok") : Ansi.Red("FAIL");
        sb.Append($"{status} dotnet restore: {restoredCount} projects");

        if (errors.Count > 0)
            sb.Append($", {errors.Count} errors");
        if (nugetWarnings.Count > 0)
            sb.Append($", {nugetWarnings.Count} NuGet vulns");
        if (duration != null)
            sb.Append($" ({duration})");
        sb.AppendLine();

        if (errors.Count > 0)
        {
            sb.AppendLine(Ansi.Red("Errors:"));
            foreach (var e in errors.Take(10))
                sb.AppendLine($"  {e}");
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"Restored\s+")]
    private static partial Regex RestoredRe();

    [GeneratedRegex(@"Time Elapsed\s+(\S+)")]
    private static partial Regex DurationRe();

    [GeneratedRegex(@"warning NU\d{4}")]
    private static partial Regex NugetWarnRe();
}
