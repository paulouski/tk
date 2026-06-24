using System.Text;
using System.Text.RegularExpressions;
using Tk.Common;

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
            var diagnostic = DiagnosticParser.Parse(line);
            if (diagnostic is not null)
            {
                var diag = diagnostic with { File = NormalizePath(diagnostic.File) };
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

        // Errors — every distinct error site shown; no cap
        if (realErrors.Count > 0)
        {
            sb.AppendLine(Ansi.Dim("---"));
            sb.AppendLine(Ansi.Red("Errors:"));
            foreach (var group in GroupByCode(realErrors))
                sb.AppendLine(FormatGroup(group, "error", capFiles: false));
        }

        // Warnings — grouped/capped (lower signal); cap is explicit via the +N note
        if (realWarnings.Count > 0)
        {
            sb.AppendLine(Ansi.Yellow("Warnings:"));
            foreach (var group in GroupByCode(realWarnings).Take(15))
                sb.AppendLine(FormatGroup(group, "warning", capFiles: true));
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

    private static string FormatGroup(DiagnosticGroup g, string kind, bool capFiles)
    {
        var code = string.IsNullOrEmpty(g.Code) ? "" : $" {g.Code}";
        // Errors: full message (no truncation). Warnings: keep 140-char cap.
        var msg = (!capFiles || g.Message.Length <= 140) ? g.Message : g.Message[..140] + "...";

        if (g.Count == 1)
        {
            var file = g.Files.Count > 0 ? $"{g.Files[0]} " : "";
            return $"  {file}{kind}{code}: {msg}";
        }

        string files;
        if (capFiles)
        {
            files = g.Files.Count switch
            {
                0 => "",
                <= 3 => $" [{string.Join(", ", g.Files)}]",
                _ => $" [{string.Join(", ", g.Files.Take(3))} +{g.Files.Count - 3}]"
            };
        }
        else
        {
            // Errors: show every file site — no truncation
            files = g.Files.Count switch
            {
                0 => "",
                _ => $" [{string.Join(", ", g.Files)}]"
            };
        }

        return $"  {kind}{code} (x{g.Count}): {msg}{files}";
    }

    private static string NormalizePath(string path)
    {
        // Shorten absolute paths to relative-ish for readability
        var idx = path.LastIndexOf("\\src\\", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = path.LastIndexOf("/src/", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? path[(idx + 1)..] : Path.GetFileName(path);
    }

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
