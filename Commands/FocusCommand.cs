using System.Text;
using Tk.Common;

namespace Tk.Commands;

public sealed class FocusCommand : ICommand
{
    public string Name => "focus";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        if (ctx.Args.Length == 0)
        {
            ctx.Err.Write("tk focus: query is required\n");
            return 1;
        }

        var query = ctx.Args[0];
        var remaining = ctx.Args[1..];
        var path = remaining.FirstOrDefault(a => !a.StartsWith('-')) ?? ".";
        var flags = remaining.Where(a => a.StartsWith('-')).ToArray();

        if (string.IsNullOrWhiteSpace(query))
        {
            ctx.Err.Write("tk focus: query is required\n");
            return 1;
        }

        if (!Directory.Exists(path))
        {
            ctx.Err.Write($"tk focus: {path}: no such directory\n");
            return 1;
        }

        var options = FocusOptions.Parse(flags);

        try
        {
            var args = new List<string>
            {
                "rg",
                "-n",
                "-F",
                "--hidden",
                "--smart-case"
            };

            foreach (var glob in new[] { ".git", "bin", "obj", "node_modules", ".idea", ".vs", "dist", "coverage" })
            {
                args.Add("--glob");
                args.Add($"!{glob}");
                args.Add("--glob");
                args.Add($"!{glob}/**");
            }

            foreach (var glob in new[] { "*.g.cs", "*.generated.cs", "*.designer.cs", "*.AssemblyInfo.cs", "GlobalUsings.g.cs" })
            {
                args.Add("--glob");
                args.Add($"!{glob}");
            }

            args.Add(query);
            args.Add(path);

            var (exitCode, stdout, stderr) = await ctx.Process.RunAsync([.. args]);
            var raw = ProcessOutput.Combine(stdout, stderr);
            if (ctx.Raw || exitCode > 1)
            {
                ctx.Out.Write(raw);
                return exitCode;
            }

            ctx.RawCharCount = raw.Length;
            ctx.RawLineCount = HiddenLinesFooter.CountLines(raw);
            ctx.Out.Write(Render(raw, exitCode, ctx.DetailLevel, options));
            return exitCode;
        }
        catch
        {
            var fallback = FallbackSearch(query, path, ctx.Raw, ctx.DetailLevel, options);
            ctx.Out.Write(fallback.Output);
            return fallback.ExitCode;
        }
    }

    private static (int ExitCode, string Output) FallbackSearch(string query, string path, bool raw, DetailLevel detail, FocusOptions options)
    {
        var ignoreCase = query.All(c => !char.IsLetter(c) || char.IsLower(c));
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var matches = new List<string>();

        foreach (var file in EnumerateFiles(path))
        {
            if (LooksBinary(file))
                continue;

            var lineNo = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNo++;
                if (!line.Contains(query, comparison))
                    continue;

                matches.Add($"{file}:{lineNo}:{line}");
                if (!raw && matches.Count >= 500)
                    break;
            }

            if (!raw && matches.Count >= 500)
                break;
        }

        var rawOut = string.Join('\n', matches);
        if (matches.Count > 0)
            rawOut += "\n";

        if (raw)
            return (matches.Count > 0 ? 0 : 1, rawOut);

        var exitCode = matches.Count > 0 ? 0 : 1;
        return (exitCode, Render(rawOut, exitCode, detail, options));
    }

    private static IEnumerable<string> EnumerateFiles(string path)
    {
        foreach (var file in Directory.GetFiles(path))
            yield return file;

        foreach (var directory in Directory.GetDirectories(path))
        {
            if (!RepoScope.ShouldIncludeDirectory(directory, includeIgnored: false, codeFocused: false))
                continue;

            foreach (var file in EnumerateFiles(directory))
                yield return file;
        }
    }

    private static bool LooksBinary(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> buffer = stackalloc byte[512];
        var read = stream.Read(buffer);
        return buffer[..read].IndexOf((byte)0) >= 0;
    }

    private static string Render(string raw, int exitCode, DetailLevel detail, FocusOptions options)
    {
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var entries = lines
            .Select(ParseMatch)
            .Where(m => m != null)
            .Cast<MatchEntry>()
            .ToList();

        if (entries.Count == 0)
            return $"focus{ScopeSuffix(options.Scope)} m=0 f=0\n";

        var prefix = PathUtils.FindCommonPrefix(entries.Select(e => e.File).Distinct());
        var files = entries
            .GroupBy(e => PathUtils.StripPrefix(e.File, prefix))
            .Select(g => BuildFileSummary(g.Key, g))
            .ToList();

        var filteredFiles = FilterByScope(files, options.Scope).ToList();
        if (filteredFiles.Count == 0)
            return $"focus{ScopeSuffix(options.Scope)} m=0 f=0\n";

        var filteredMatches = entries.Count(e => IsVisible(e.Kind, options.Scope));
        var limit = detail == DetailLevel.More ? 8 : 5;
        var docLimit = detail == DetailLevel.More ? 4 : 2;
        var sampleLimit = detail == DetailLevel.More ? 6 : 3;
        var sb = new StringBuilder();
        sb.AppendLine(BuildHeader(files, filteredFiles, entries.Count, filteredMatches, options.Scope));

        var shownFiles = new HashSet<string>(StringComparer.Ordinal);
        if (options.Scope == FocusScope.Default)
        {
            var codeFiles = filteredFiles.Where(f => f.Kind == FocusKind.Code).ToList();
            if (codeFiles.Count > 0)
            {
                AppendSection(sb, "code", codeFiles, limit, shownFiles);
            }
            else
            {
                AppendSection(sb, "top", filteredFiles, limit, shownFiles);
            }

            if (detail == DetailLevel.More)
            {
                var docFiles = filteredFiles.Where(f => f.Kind == FocusKind.Docs).ToList();
                if (docFiles.Count > 0)
                    AppendSection(sb, "docs", docFiles, docLimit, shownFiles);
            }
        }
        else
        {
            AppendSection(sb, "top", filteredFiles, limit, shownFiles);
        }

        if (!options.FilesOnly)
            AppendSamples(sb, filteredFiles, shownFiles, sampleLimit);

        var extra = filteredFiles.Count - shownFiles.Count;
        if (extra > 0)
            sb.AppendLine(Ansi.Dim($"+{extra} more files"));

        return sb.ToString();
    }

    private static string BuildHeader(
        IReadOnlyCollection<FileSummary> allFiles,
        IReadOnlyCollection<FileSummary> visibleFiles,
        int totalMatches,
        int visibleMatches,
        FocusScope scope)
    {
        if (scope == FocusScope.Default || scope == FocusScope.All)
        {
            var codeCount = allFiles.Count(f => f.Kind == FocusKind.Code);
            var docCount = allFiles.Count(f => f.Kind == FocusKind.Docs);
            var logCount = allFiles.Count(f => f.Kind == FocusKind.Logs);
            var genCount = allFiles.Count(f => f.Kind == FocusKind.Generated);
            var otherCount = allFiles.Count - codeCount - docCount - logCount - genCount;

            var parts = new List<string>
            {
                $"focus{ScopeSuffix(scope)} m={totalMatches}",
                $"f={allFiles.Count}",
                $"code={codeCount}",
                $"docs={docCount}",
                $"logs={logCount}"
            };

            if (genCount > 0)
                parts.Add($"gen={genCount}");
            if (otherCount > 0)
                parts.Add($"other={otherCount}");

            return string.Join(' ', parts);
        }

        return $"focus{ScopeSuffix(scope)} m={visibleMatches} f={visibleFiles.Count}";
    }

    private static void AppendSection(
        StringBuilder sb,
        string label,
        IReadOnlyList<FileSummary> files,
        int limit,
        ISet<string> shownFiles)
    {
        var top = files
            .Take(limit)
            .Select(file =>
            {
                shownFiles.Add(file.Path);
                return $"{file.Path}({file.Count})";
            });

        sb.AppendLine($"{label}={string.Join(",", top)}");
    }

    private static void AppendSamples(
        StringBuilder sb,
        IReadOnlyList<FileSummary> files,
        ISet<string> shownFiles,
        int limit)
    {
        var samples = files
            .Where(file => shownFiles.Contains(file.Path))
            .Take(limit)
            .ToList();

        if (samples.Count == 0)
            return;

        sb.AppendLine("samples:");
        foreach (var sample in samples)
            sb.AppendLine($"  {sample.Path}:{sample.SampleLine} {sample.Sample}");
    }

    private static FileSummary BuildFileSummary(string relativePath, IGrouping<string, MatchEntry> group)
    {
        var first = group.OrderBy(entry => entry.Line).First();
        return new FileSummary(
            relativePath,
            Classify(relativePath),
            group.Count(),
            first.Line,
            Compact(first.Content));
    }

    private static IEnumerable<FileSummary> FilterByScope(IEnumerable<FileSummary> files, FocusScope scope)
    {
        var ordered = files
            .Where(file => IsVisible(file.Kind, scope))
            .OrderBy(file => Rank(file.Kind, scope))
            .ThenByDescending(file => file.Count)
            .ThenBy(file => file.Path, StringComparer.Ordinal);

        return ordered;
    }

    private static bool IsVisible(FocusKind kind, FocusScope scope) => scope switch
    {
        FocusScope.Docs => kind == FocusKind.Docs,
        FocusScope.CodeOnly => kind == FocusKind.Code,
        _ => true
    };

    private static int Rank(FocusKind kind, FocusScope scope) => scope switch
    {
        FocusScope.Default => kind switch
        {
            FocusKind.Code => 0,
            FocusKind.Other => 1,
            FocusKind.Docs => 2,
            FocusKind.Logs => 3,
            FocusKind.Generated => 4,
            _ => 5
        },
        FocusScope.All => kind switch
        {
            FocusKind.Code => 0,
            FocusKind.Docs => 1,
            FocusKind.Other => 2,
            FocusKind.Logs => 3,
            FocusKind.Generated => 4,
            _ => 5
        },
        _ => 0
    };

    private static FocusKind Classify(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');

        if (normalized.Contains("/logs/", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            return FocusKind.Logs;
        }

        if (normalized.StartsWith("_local/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(".claude/", StringComparison.OrdinalIgnoreCase) ||
            RepoScope.IsDocFile(relativePath))
        {
            return FocusKind.Docs;
        }

        var fileName = Path.GetFileName(normalized);
        if (normalized.Contains("/generated/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("generated/", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Generated", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            return FocusKind.Generated;
        }

        if (RepoScope.IsCodeFile(relativePath))
            return FocusKind.Code;

        return FocusKind.Other;
    }

    private static string Compact(string content)
    {
        var compact = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 100 ? compact : $"{compact[..97]}...";
    }

    private static string ScopeSuffix(FocusScope scope) => scope switch
    {
        FocusScope.All => " all",
        FocusScope.Docs => " docs",
        FocusScope.CodeOnly => " code",
        _ => string.Empty
    };

    private static MatchEntry? ParseMatch(string line)
    {
        var firstColon = line.IndexOf(':');
        if (firstColon < 0)
            return null;

        var secondColon = line.IndexOf(':', firstColon + 1);
        if (secondColon < 0)
            return null;

        var file = line[..firstColon];
        if (!int.TryParse(line[(firstColon + 1)..secondColon], out var lineNo))
            return null;

        return new MatchEntry(file, lineNo, line[(secondColon + 1)..], Classify(file));
    }

    private sealed record MatchEntry(string File, int Line, string Content, FocusKind Kind);

    private sealed record FileSummary(string Path, FocusKind Kind, int Count, int SampleLine, string Sample);

    private enum FocusKind
    {
        Code,
        Docs,
        Logs,
        Other,
        Generated
    }

    private enum FocusScope
    {
        Default,
        All,
        Docs,
        CodeOnly
    }

    private sealed record FocusOptions(bool FilesOnly, FocusScope Scope)
    {
        public static FocusOptions Parse(IEnumerable<string> flags)
        {
            var set = new HashSet<string>(flags, StringComparer.OrdinalIgnoreCase);
            var scope = FocusScope.Default;

            if (set.Contains("--all"))
                scope = FocusScope.All;
            else if (set.Contains("--docs"))
                scope = FocusScope.Docs;
            else if (set.Contains("--code-only") || set.Contains("--code"))
                scope = FocusScope.CodeOnly;

            return new FocusOptions(
                set.Contains("--files-only"),
                scope);
        }
    }
}
