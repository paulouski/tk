using System.Text;
using Tk.Common;
using Tk.Filters;

namespace Tk.Commands;

public sealed class FilesCommand : ICommand
{
    public string Name => "files";

    public async Task<int> RunAsync(CommandContext ctx) => await RunAsync(ctx, unityMode: false);

    public async Task<int> RunAsync(CommandContext ctx, bool unityMode)
    {
        var (output, exitCode) = await RenderAsync(ctx.Args, ctx.Raw, ctx.DetailLevel, ctx.Process, unityMode);
        ctx.Out.Write(output);
        return exitCode;
    }

    private static async Task<(string Output, int ExitCode)> RenderAsync(string[] args, bool raw, DetailLevel detail, IProcessRunner runner, bool unityMode = false)
    {
        var path = FindPathArg(args) ?? ".";
        var flags = args;

        if (!Directory.Exists(path))
            return ($"tk files: {path}: no such directory\n", 1);

        var changedOnly = flags.Contains("--changed");
        var codeFocused = flags.Contains("--code");
        var extension = ParseExtension(flags);
        var top = ParseTop(flags) ?? (raw ? 50 : detail == DetailLevel.More ? 20 : 8);
        List<string> files;

        if (changedOnly)
        {
            files = await GetChangedFilesAsync(path, runner);
        }
        else
        {
            files = EnumerateFiles(path, includeIgnored: raw, codeFocused, unityMode).ToList();
        }

        if (!string.IsNullOrEmpty(extension))
            files = files.Where(f => string.Equals(Path.GetExtension(f), extension, StringComparison.OrdinalIgnoreCase)).ToList();

        if (codeFocused)
            files = files.Where(f => RepoScope.IsCodeFile(f, unityMode)).ToList();

        var relative = files
            .Select(f => MakeRelative(path, f))
            .OrderBy(f => RepoScope.ScoreFile(f, codeFocused))
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToList();

        var groups = relative
            .GroupBy(TopGroup)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(detail == DetailLevel.More ? 6 : 3)
            .Select(g => $"{g.Key}({g.Count()})")
            .ToList();

        var sb = new StringBuilder();
        sb.Append(codeFocused ? $"files code n={relative.Count}" : $"files n={relative.Count}");
        if (!string.IsNullOrEmpty(extension))
            sb.Append($" ext={extension.TrimStart('.')}");
        if (changedOnly)
            sb.Append(" changed=1");
        sb.AppendLine();

        if (groups.Count > 0)
            sb.AppendLine($"top={string.Join(",", groups)}");

        if (relative.Count > 0)
        {
            sb.AppendLine("list:");
            foreach (var file in relative.Take(top))
                sb.AppendLine($"  {file}");
        }

        var extra = relative.Count - top;
        if (extra > 0)
            sb.AppendLine(Ansi.Dim($"+{extra} more files"));

        return (sb.ToString(), 0);
    }

    private static IEnumerable<string> EnumerateFiles(string path, bool includeIgnored, bool codeFocused, bool unityMode = false)
    {
        foreach (var file in Directory.GetFiles(path))
        {
            if (!RepoScope.ShouldIncludeFile(file, codeFocused, unityMode))
                continue;

            yield return file;
        }

        foreach (var directory in Directory.GetDirectories(path))
        {
            if (!RepoScope.ShouldIncludeDirectory(directory, includeIgnored, codeFocused, unityMode))
                continue;

            foreach (var file in EnumerateFiles(directory, includeIgnored, codeFocused, unityMode))
                yield return file;
        }
    }

    private static async Task<List<string>> GetChangedFilesAsync(string path, IProcessRunner runner)
    {
        try
        {
            var (exitCode, stdout, stderr) = await runner.RunAsync(["git", "-C", path, "status", "--porcelain=v1"]);
            if (exitCode != 0)
                return [];

            return ProcessOutput.Combine(stdout, stderr)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(GitPorcelain.ParseLine)
                .Where(e => e is not null)
                .Select(e => Path.Combine(path, e!.Path))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string MakeRelative(string basePath, string fullPath)
    {
        var relative = Path.GetRelativePath(basePath, fullPath);
        return PathUtils.StripPrefix(relative, "");
    }

    private static string TopGroup(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slash = normalized.IndexOf('/');
        return slash > 0 ? normalized[..slash] : "root";
    }

    private static int? ParseTop(string[] flags)
    {
        for (var i = 0; i < flags.Length - 1; i++)
        {
            if (flags[i] == "--top" && int.TryParse(flags[i + 1], out var top))
                return Math.Max(1, top);
        }

        return null;
    }

    private static string? ParseExtension(string[] flags)
    {
        for (var i = 0; i < flags.Length - 1; i++)
        {
            if (flags[i] == "--ext")
            {
                var ext = flags[i + 1];
                if (string.IsNullOrWhiteSpace(ext))
                    return null;

                return ext.StartsWith('.') ? ext : "." + ext;
            }
        }

        return null;
    }

    private static string? FindPathArg(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith('-'))
            {
                if ((arg == "--depth" || arg == "--top" || arg == "--ext") && i + 1 < args.Length)
                    i++;
                continue;
            }

            return arg;
        }

        return null;
    }
}
