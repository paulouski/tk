using System.Text;
using System.Text.RegularExpressions;

namespace Tk.Filters;

public sealed partial class DotnetBuildFilter : IOutputFilter
{
    public string Apply(string raw, int exitCode)
    {
        var lines = raw.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var errors = new List<Diagnostic>();
        var warnings = new List<Diagnostic>();
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? duration = null;
        bool succeeded = exitCode == 0;

        foreach (var line in lines)
        {
            // Extract diagnostics: file(line,col): warning/error CODE: message [project]
            var m = DiagnosticRe().Match(line);
            if (m.Success)
            {
                var diag = new Diagnostic(
                    File: NormalizePath(m.Groups["file"].Value.Trim()),
                    Line: int.TryParse(m.Groups["line"].Value, out var l) ? l : 0,
                    Column: int.TryParse(m.Groups["col"].Value, out var c) ? c : 0,
                    Kind: m.Groups["kind"].Value.ToLowerInvariant(),
                    Code: m.Groups["code"].Value,
                    Message: m.Groups["msg"].Value.Trim(),
                    Project: ExtractProject(m.Groups["proj"].Value)
                );

                if (diag.Kind == "error")
                    errors.Add(diag);
                else
                    warnings.Add(diag);

                continue;
            }

            // Extract tool diagnostics: "MSBUILD : error MSB1009: message"
            var toolMatch = ToolDiagnosticRe().Match(line);
            if (toolMatch.Success)
            {
                var diag = new Diagnostic(
                    File: toolMatch.Groups["source"].Value.Trim(),
                    Line: 0,
                    Column: 0,
                    Kind: toolMatch.Groups["kind"].Value.ToLowerInvariant(),
                    Code: toolMatch.Groups["code"].Value,
                    Message: toolMatch.Groups["msg"].Value.Trim(),
                    Project: ""
                );

                if (diag.Kind == "error")
                    errors.Add(diag);
                else
                    warnings.Add(diag);

                continue;
            }

            // Extract project build lines: "ProjectName -> output.dll"
            var projMatch = ProjectBuildRe().Match(line);
            if (projMatch.Success)
            {
                projects.Add(projMatch.Groups[1].Value.Trim());
                continue;
            }

            // Extract duration
            var durMatch = DurationRe().Match(line);
            if (durMatch.Success)
                duration = durMatch.Groups[1].Value;
        }

        // Partition NuGet vulnerabilities from real diagnostics
        var (realErrors, nugetErrorVulns) = PartitionNugetVulns(errors);
        var (realWarnings, nugetWarnVulns) = PartitionNugetVulns(warnings);
        var allNugetVulns = nugetErrorVulns.Concat(nugetWarnVulns).ToList();
        var totalNuget = allNugetVulns.Sum(g => g.Total);

        var sb = new StringBuilder();
        var status = succeeded ? Ansi.Green("ok") : Ansi.Red("FAIL");
        var projectCount = projects.Count;

        sb.Append($"{status} build p={projectCount}");
        if (!succeeded || realErrors.Count > 0 || realWarnings.Count > 0 || totalNuget > 0)
        {
            sb.Append($" e={realErrors.Count} w={realWarnings.Count}");
            if (totalNuget > 0)
                sb.Append($" nu={totalNuget}");
        }
        if (duration != null)
            sb.Append($" t={duration}");
        sb.AppendLine();

        if (!succeeded && realErrors.Count == 0 && realWarnings.Count == 0)
            AppendRawTail(sb, lines, 12);

        // Errors (grouped by code)
        if (realErrors.Count > 0)
        {
            sb.AppendLine(Ansi.Dim("---"));
            sb.AppendLine(Ansi.Red("Errors:"));
            foreach (var group in GroupByCode(realErrors).Take(20))
                sb.AppendLine(FormatGroup(group, "error"));
        }

        // Warnings (grouped by code)
        if (realWarnings.Count > 0)
        {
            sb.AppendLine(Ansi.Yellow("Warnings:"));
            foreach (var group in GroupByCode(realWarnings).Take(15))
                sb.AppendLine(FormatGroup(group, "warning"));
        }

        // NuGet vulnerability summary
        if (allNugetVulns.Count > 0)
        {
            sb.AppendLine(Ansi.Yellow("NuGet Vulnerabilities:"));
            foreach (var g in allNugetVulns)
                sb.AppendLine($"  {g.Package} {g.Version}: {g.Total} ({g.Summary()})");
        }

        return sb.ToString();
    }

    private static (List<Diagnostic> Regular, List<NugetVulnGroup> Vulns) PartitionNugetVulns(
        List<Diagnostic> diagnostics)
    {
        var regular = new List<Diagnostic>();
        var vulnMap = new Dictionary<string, NugetVulnGroup>(StringComparer.Ordinal);

        foreach (var d in diagnostics)
        {
            if (IsNugetVuln(d))
            {
                var (pkg, ver, severity) = ExtractNugetInfo(d);
                var key = $"{pkg}@{ver}";
                if (!vulnMap.TryGetValue(key, out var group))
                {
                    group = new NugetVulnGroup(pkg, ver);
                    vulnMap[key] = group;
                }
                group.Add(severity);
            }
            else
            {
                regular.Add(d);
            }
        }

        return (regular, vulnMap.Values.ToList());
    }

    private static bool IsNugetVuln(Diagnostic d) =>
        d.Code is "NU1901" or "NU1902" or "NU1903" or "NU1904"
        || (d.Code == "" && d.Message.Contains("severity vulnerability"));

    private static (string Package, string Version, string Severity) ExtractNugetInfo(Diagnostic d)
    {
        var msg = d.Message;
        var pkg = "";
        var ver = "";

        var pkgStart = msg.IndexOf("Package '", StringComparison.Ordinal);
        if (pkgStart >= 0)
        {
            pkgStart += 9;
            var pkgEnd = msg.IndexOf('\'', pkgStart);
            if (pkgEnd > pkgStart) pkg = msg[pkgStart..pkgEnd];
        }

        // Version follows after "' "
        var verStart = msg.IndexOf("' ", StringComparison.Ordinal);
        if (verStart >= 0)
        {
            verStart += 2;
            var verEnd = msg.IndexOf(' ', verStart);
            if (verEnd > verStart) ver = msg[verStart..verEnd];
        }

        var severity = msg.Contains("critical") ? "critical"
            : msg.Contains("high") ? "high"
            : msg.Contains("moderate") ? "moderate"
            : "low";

        return (pkg, ver, severity);
    }

    private static IEnumerable<DiagnosticGroup> GroupByCode(List<Diagnostic> diagnostics)
    {
        var groups = new Dictionary<string, DiagnosticGroup>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var d in diagnostics)
        {
            var key = string.IsNullOrEmpty(d.Code) ? d.Message[..Math.Min(80, d.Message.Length)] : d.Code;
            if (!groups.TryGetValue(key, out var group))
            {
                group = new DiagnosticGroup(d.Code, d.Message);
                groups[key] = group;
                order.Add(key);
            }
            group.Count++;
            var fileName = Path.GetFileName(d.File);
            if (!string.IsNullOrEmpty(fileName) && !group.Files.Contains(fileName))
                group.Files.Add(fileName);
        }

        return order.Select(k => groups[k]);
    }

    private static string FormatGroup(DiagnosticGroup g, string kind)
    {
        var code = string.IsNullOrEmpty(g.Code) ? "" : $" {g.Code}";
        var msg = g.Message.Length > 140 ? g.Message[..140] + "..." : g.Message;

        if (g.Count == 1)
        {
            var file = g.Files.Count > 0 ? $"{g.Files[0]} " : "";
            return $"  {file}{kind}{code}: {msg}";
        }

        var files = g.Files.Count switch
        {
            0 => "",
            <= 3 => $" [{string.Join(", ", g.Files)}]",
            _ => $" [{string.Join(", ", g.Files.Take(3))} +{g.Files.Count - 3}]"
        };

        return $"  {kind}{code} (x{g.Count}): {msg}{files}";
    }

    private static string NormalizePath(string path)
    {
        // Shorten absolute paths to relative-ish for readability
        var idx = path.LastIndexOf("\\src\\", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = path.LastIndexOf("/src/", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? path[(idx + 1)..] : Path.GetFileName(path);
    }

    private static string ExtractProject(string raw)
    {
        // [C:\path\to\Project.csproj] -> Project
        var trimmed = raw.Trim().Trim('[', ']');
        return Path.GetFileNameWithoutExtension(trimmed);
    }

    [GeneratedRegex(@"^(?<file>[^\r\n(]+)\((?<line>\d+),(?<col>\d+)\):\s*(?<kind>error|warning)\s+(?<code>[A-Za-z]*\d*)\s*:\s*(?<msg>[^\[]+?)(?<proj>\[[^\]]*\])?\s*$")]
    private static partial Regex DiagnosticRe();

    [GeneratedRegex(@"^(?<source>.+?)\s*:\s*(?<kind>error|warning)\s+(?<code>[A-Za-z]*\d+)\s*:\s*(?<msg>.+)$")]
    private static partial Regex ToolDiagnosticRe();

    [GeneratedRegex(@"^\s+(.+?)\s+->\s+")]
    private static partial Regex ProjectBuildRe();

    [GeneratedRegex(@"Time Elapsed\s+(\S+)")]
    private static partial Regex DurationRe();

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

    private record Diagnostic(string File, int Line, int Column, string Kind, string Code, string Message, string Project);

    private class DiagnosticGroup(string code, string message)
    {
        public string Code { get; } = code;
        public string Message { get; } = message;
        public int Count { get; set; }
        public List<string> Files { get; } = [];
    }

    private class NugetVulnGroup(string package, string version)
    {
        public string Package { get; } = package;
        public string Version { get; } = version;
        public int Low { get; set; }
        public int Moderate { get; set; }
        public int High { get; set; }
        public int Critical { get; set; }
        public int Total => Low + Moderate + High + Critical;

        public void Add(string severity)
        {
            switch (severity)
            {
                case "critical": Critical++; break;
                case "high": High++; break;
                case "moderate": Moderate++; break;
                default: Low++; break;
            }
        }

        public string Summary()
        {
            var parts = new List<string>();
            if (Low > 0) parts.Add($"{Low} low");
            if (Moderate > 0) parts.Add($"{Moderate} moderate");
            if (High > 0) parts.Add($"{High} high");
            if (Critical > 0) parts.Add($"{Critical} critical");
            return string.Join(", ", parts);
        }
    }
}
