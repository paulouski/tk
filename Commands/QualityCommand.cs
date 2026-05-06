using System.Text;
using System.Text.RegularExpressions;
using Tk.Common;
using Tk.Filters;

namespace Tk.Commands;

public sealed class QualityCommand : ICommand
{
    public string Name => "quality";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        var (exitCode, output) = await RunQualityAsync(ctx.Args, ctx.Process);
        ctx.Out.Write(output);
        return exitCode;
    }

    private static async Task<(int ExitCode, string Output)> RunQualityAsync(string[] args, IProcessRunner runner)
    {
        var options = QualityOptions.Parse(args);
        var root = Path.GetFullPath(options.Path);
        if (!Directory.Exists(root) && !File.Exists(root))
            return (1, $"tk quality: {options.Path}: no such file or directory\n");

        var target = ResolveDotnetTarget(root);
        var qualityTargets = FindQualityTargets(root);
        var changeSet = await LoadChangedFilesAsync(root, runner);
        var sb = new StringBuilder();
        var exitCode = 0;

        sb.AppendLine(target is null
            ? $"quality root={ShortPath(root)} target=none"
            : $"quality root={ShortPath(root)} target={Path.GetFileName(target)}");
        sb.AppendLine(qualityTargets is null
            ? "local analyzers=none"
            : $"local analyzers={ShortPath(qualityTargets)}");
        sb.AppendLine(changeSet.IsAvailable
            ? $"changed files={changeSet.Files.Count}"
            : "changed files=unknown");

        if (target is null)
        {
            sb.AppendLine("skip build reason=no-sln-or-csproj");
            sb.AppendLine("skip format reason=no-sln-or-csproj");
            sb.AppendLine("skip test reason=no-sln-or-csproj");
            return (0, sb.ToString());
        }

        if (options.Build)
        {
            var buildArgs = new List<string> { "dotnet", "build", target };
            AddQualityTargets(buildArgs, qualityTargets);

            var build = await RunQualityBuildAsync(buildArgs.ToArray(), changeSet, options.ShowAll, runner);
            sb.Append(build.Output);
            if (build.EffectiveExitCode != 0)
                exitCode = build.ExitCode;
        }
        else
        {
            sb.AppendLine("skip build");
        }

        if (options.Format)
        {
            var format = await RunFormatAsync(target, changeSet, options.ShowAll, runner);
            sb.Append(format.Output);
            if (format.EffectiveExitCode != 0 && exitCode == 0)
                exitCode = format.ExitCode;
        }
        else
        {
            sb.AppendLine("skip format");
        }

        if (options.Test || options.TestFilter is not null)
        {
            var testArgs = new List<string> { "dotnet", "test", target, "--no-build" };
            if (options.TestFilter is not null)
            {
                testArgs.Add("--filter");
                testArgs.Add(options.TestFilter);
            }

            var test = await RunFilteredAsync(testArgs.ToArray(), new DotnetTestFilter(), runner);
            sb.Append(test.Output);
            if (test.ExitCode != 0 && exitCode == 0)
                exitCode = test.ExitCode;
        }
        else
        {
            sb.AppendLine("skip test reason=use---test-or---test-filter");
        }

        return (exitCode, sb.ToString());
    }

    private static async Task<(int ExitCode, string Output)> RunFilteredAsync(string[] args, IOutputFilter filter, IProcessRunner runner)
    {
        var (exitCode, stdout, stderr) = await runner.RunAsync(args);
        var raw = ProcessOutput.Combine(stdout, stderr);
        return (exitCode, filter.Apply(raw, exitCode));
    }

    private static async Task<QualityStepResult> RunQualityBuildAsync(
        string[] args,
        ChangeSet changeSet,
        bool showAll,
        IProcessRunner runner)
    {
        var (exitCode, stdout, stderr) = await runner.RunAsync(args);
        var raw = ProcessOutput.Combine(stdout, stderr);
        var filtered = new DotnetBuildFilter().Apply(raw, exitCode);
        var summary = filtered.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        var diagnostics = ParseDiagnostics(raw);
        var errors = diagnostics.Where(d => d.Kind == "error").ToArray();
        var warnings = diagnostics.Where(d => d.Kind == "warning").ToArray();
        var changedErrors = changeSet.Filter(errors).ToArray();
        var changedWarnings = changeSet.Filter(warnings).ToArray();

        var sb = new StringBuilder();
        sb.AppendLine(summary);
        if (changeSet.IsAvailable)
        {
            sb.AppendLine(
                $"build changed_e={changedErrors.Length} changed_w={changedWarnings.Length} baseline_e={errors.Length - changedErrors.Length} baseline_w={warnings.Length - changedWarnings.Length}");
        }

        var errorsToShow = showAll || !changeSet.IsAvailable ? errors : changedErrors;
        var warningsToShow = showAll || !changeSet.IsAvailable ? warnings : changedWarnings;

        if (errorsToShow.Length > 0)
        {
            sb.AppendLine("---");
            sb.AppendLine(showAll ? "Errors:" : "Errors changed:");
            AppendDiagnosticGroups(sb, errorsToShow, maxGroups: 8, "error");
        }

        if (warningsToShow.Length > 0)
        {
            sb.AppendLine(showAll ? "Warnings:" : "Warnings changed:");
            AppendDiagnosticGroups(sb, warningsToShow, maxGroups: errorsToShow.Length > 0 ? 5 : 8, "warning");
        }

        if (!showAll && changeSet.IsAvailable && errorsToShow.Length == 0 && warningsToShow.Length == 0 &&
            (errors.Length > 0 || warnings.Length > 0))
        {
            sb.AppendLine("skip build details reason=no-changed-file-diagnostics use=--all");
        }

        if (exitCode != 0 && errors.Length == 0)
        {
            var tail = raw.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .TakeLast(8);

            sb.AppendLine("--- raw tail ---");
            foreach (var line in tail)
                sb.AppendLine($"  {line}");
        }

        var effectiveExitCode = exitCode;
        if (!showAll && changeSet.IsAvailable && changedErrors.Length == 0)
            effectiveExitCode = 0;

        return new QualityStepResult(exitCode, effectiveExitCode, sb.ToString());
    }

    private static async Task<QualityStepResult> RunFormatAsync(
        string target,
        ChangeSet changeSet,
        bool showAll,
        IProcessRunner runner)
    {
        var (exitCode, stdout, stderr) = await runner.RunAsync(
            ["dotnet", "format", target, "--verify-no-changes", "--no-restore"]);

        if (exitCode == 0)
            return new QualityStepResult(0, 0, "ok format\n");

        var raw = ProcessOutput.Combine(stdout, stderr);
        var formatIssues = ParseFormatIssues(raw);
        if (formatIssues.Count > 0)
        {
            var changedIssueCount = changeSet.Filter(formatIssues).Count();
            var effectiveExitCode = showAll || !changeSet.IsAvailable || changedIssueCount > 0 ? exitCode : 0;
            return new QualityStepResult(exitCode, effectiveExitCode, FormatFormatIssues(formatIssues, changeSet, showAll));
        }

        var lines = raw
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .TakeLast(8)
            .ToArray();

        var sb = new StringBuilder();
        sb.AppendLine("FAIL format e=1");
        foreach (var line in lines)
            sb.AppendLine($"  {line}");

        return new QualityStepResult(exitCode, exitCode, sb.ToString());
    }

    private static List<Diagnostic> ParseDiagnostics(string raw)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var line in raw.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            var diagnostic = DiagnosticParser.Parse(line);
            if (diagnostic is not null)
                diagnostics.Add(diagnostic);
        }

        return diagnostics;
    }

    private static void AppendDiagnosticGroups(StringBuilder sb, Diagnostic[] diagnostics, int maxGroups, string kind)
    {
        var groups = diagnostics
            .GroupBy(d => string.IsNullOrEmpty(d.Code) ? d.Message : d.Code)
            .Select(g => new
            {
                Key = g.Key,
                Count = g.Count(),
                Sample = g.First(),
                Files = g.Select(d => d.File)
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Select(Path.GetFileName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray()
            })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToArray();

        foreach (var group in groups.Take(maxGroups))
        {
            var code = string.IsNullOrEmpty(group.Sample.Code) ? "" : $" {group.Sample.Code}";
            var files = group.Files.Length == 0 ? "" : $" files={string.Join(",", group.Files)}";
            sb.AppendLine($"  {kind}{code} x{group.Count}:{files} {Truncate(group.Sample.Message, 110)}");
        }

        if (groups.Length > maxGroups)
            sb.AppendLine($"  ... +{groups.Length - maxGroups} groups");
    }

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "..." : s;

    private static List<FormatIssue> ParseFormatIssues(string raw)
    {
        var issues = new List<FormatIssue>();
        foreach (var line in raw.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            var match = FormatIssueRe.Match(line);
            if (!match.Success)
                continue;

            issues.Add(new FormatIssue(
                File: Path.GetFileName(match.Groups["file"].Value),
                Line: int.TryParse(match.Groups["line"].Value, out var lineNumber) ? lineNumber : 0));
        }

        return issues;
    }

    private static string FormatFormatIssues(List<FormatIssue> issues, ChangeSet changeSet, bool showAll)
    {
        var changedIssues = changeSet.Filter(issues).ToArray();
        var issuesToShow = showAll || !changeSet.IsAvailable ? issues.ToArray() : changedIssues;
        var groups = issuesToShow
            .GroupBy(i => i.File)
            .Select(g => new
            {
                File = g.Key,
                Count = g.Count(),
                Lines = g.Select(i => i.Line).Where(l => l > 0).Distinct().Order().Take(8).ToArray()
            })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.File, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sb = new StringBuilder();
        if (changeSet.IsAvailable)
        {
            var baselineCount = issues.Count - changedIssues.Length;
            var changedFileCount = changedIssues.Select(i => i.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var baselineFileCount = issues.Select(i => i.File).Except(changedIssues.Select(i => i.File), StringComparer.OrdinalIgnoreCase).Count();
            sb.AppendLine(
                $"FAIL format changed_files={changedFileCount} changed_n={changedIssues.Length} baseline_files={baselineFileCount} baseline_n={baselineCount}");
        }
        else
        {
            sb.AppendLine($"FAIL format files={groups.Length} n={issues.Count}");
        }

        if (groups.Length == 0)
        {
            sb.AppendLine("skip format details reason=no-changed-file-issues use=--all");
            return sb.ToString();
        }

        foreach (var group in groups.Take(8))
        {
            var lines = group.Lines.Length == 0 ? "" : $" lines={string.Join(",", group.Lines)}";
            sb.AppendLine($"  {group.File} n={group.Count}{lines}");
        }

        if (groups.Length > 8)
            sb.AppendLine($"  ... +{groups.Length - 8} files");

        return sb.ToString();
    }

    private static void AddQualityTargets(List<string> args, string? qualityTargets)
    {
        if (qualityTargets is null)
            return;

        args.Add($"-p:CustomAfterMicrosoftCSharpTargets={qualityTargets}");
    }

    private static string? FindQualityTargets(string root)
    {
        var directory = File.Exists(root) ? Path.GetDirectoryName(root)! : root;
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "_local", "tk-quality", "quality.targets");
            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        return null;
    }

    private static async Task<ChangeSet> LoadChangedFilesAsync(string root, IProcessRunner runner)
    {
        var directory = File.Exists(root) ? Path.GetDirectoryName(root)! : root;
        var repoRoot = FindGitRoot(directory);
        if (repoRoot is null)
            return ChangeSet.Unavailable;

        var (exitCode, stdout, stderr) = await runner.RunAsync(
            ["git", "-C", repoRoot, "status", "--porcelain=v1"]);
        if (exitCode != 0)
            return ChangeSet.Unavailable;

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in ProcessOutput.Combine(stdout, stderr).Split('\n').Select(l => l.TrimEnd('\r')))
        {
            var entry = GitPorcelain.ParseLine(rawLine);
            if (entry is null)
                continue;

            files.Add(NormalizeFullPath(Path.Combine(repoRoot, entry.Path)));
        }

        return new ChangeSet(repoRoot, files);
    }

    private static string? FindGitRoot(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;

            current = current.Parent;
        }

        return null;
    }

    private static string? ResolveDotnetTarget(string root)
    {
        var directory = File.Exists(root) ? Path.GetDirectoryName(root)! : root;
        if (File.Exists(root) && (root.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                                  root.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            return root;
        }

        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            var projects = Directory.EnumerateFiles(current.FullName, "*.csproj", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (projects.Length == 1)
                return projects[0];

            var solutions = Directory.EnumerateFiles(current.FullName, "*.sln", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (solutions.Length == 1)
                return solutions[0];

            if (IsRepoBoundary(current.FullName))
                return projects.FirstOrDefault() ?? solutions.FirstOrDefault();

            current = current.Parent;
        }

        return null;
    }

    private static bool IsRepoBoundary(string directory) =>
        Directory.Exists(Path.Combine(directory, ".git")) ||
        File.Exists(Path.Combine(directory, "global.json")) ||
        File.Exists(Path.Combine(directory, "Directory.Build.props"));

    private static string ShortPath(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static string NormalizeFullPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static readonly Regex FormatIssueRe = new(
        @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s*error\s+WHITESPACE:",
        RegexOptions.Compiled);

    private sealed record FormatIssue(string File, int Line);
    private sealed record QualityStepResult(int ExitCode, int EffectiveExitCode, string Output);

    private sealed class ChangeSet(string RepoRoot, HashSet<string> Files)
    {
        public static ChangeSet Unavailable { get; } = new("", []);
        public bool IsAvailable => !string.IsNullOrEmpty(RepoRoot);
        public HashSet<string> Files { get; } = Files;

        public IEnumerable<Diagnostic> Filter(IEnumerable<Diagnostic> diagnostics) =>
            IsAvailable ? diagnostics.Where(d => IsChanged(d.File)) : diagnostics;

        public IEnumerable<FormatIssue> Filter(IEnumerable<FormatIssue> issues) =>
            IsAvailable ? issues.Where(i => IsChanged(i.File)) : issues;

        private bool IsChanged(string file)
        {
            if (string.IsNullOrWhiteSpace(file))
                return false;

            var fullPath = Path.IsPathRooted(file)
                ? NormalizeFullPath(file)
                : NormalizeFullPath(Path.Combine(RepoRoot, file));

            return Files.Contains(fullPath);
        }
    }

    private sealed record QualityOptions(
        string Path,
        bool Build,
        bool Format,
        bool Test,
        string? TestFilter,
        bool ShowAll)
    {
        public static QualityOptions Parse(string[] args)
        {
            var path = ".";
            var build = true;
            var format = true;
            var test = false;
            var showAll = false;
            string? testFilter = null;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--no-build":
                        build = false;
                        break;
                    case "--no-format":
                        format = false;
                        break;
                    case "--test":
                    case "--deep":
                        test = true;
                        break;
                    case "--all":
                        showAll = true;
                        break;
                    case "--test-filter":
                        if (i + 1 >= args.Length)
                            throw new ArgumentException("tk quality: --test-filter requires a value");
                        testFilter = args[++i];
                        break;
                    default:
                        if (!arg.StartsWith('-'))
                            path = arg;
                        break;
                }
            }

            return new QualityOptions(path, build, format, test, testFilter, showAll);
        }
    }
}
