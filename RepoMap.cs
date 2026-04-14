using System.Text;
using Tk.Filters;

namespace Tk;

public static class RepoMap
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".idea", ".vs", "dist", "coverage"
    };

    public static string RenderTree(string[] args, CliOptions cliOptions)
    {
        var path = args.FirstOrDefault(a => !a.StartsWith('-')) ?? ".";
        var flags = args;

        if (!Directory.Exists(path))
            return $"tk tree: {path}: no such directory\n";

        var includeIgnored = cliOptions.Raw || flags.Contains("--all");
        var maxDepth = ParseDepth(flags) ?? (cliOptions.Raw ? 5 : cliOptions.DetailLevel == DetailLevel.More ? 3 : 2);
        var topFiles = cliOptions.Raw ? 12 : cliOptions.DetailLevel == DetailLevel.More ? 8 : 5;
        var root = BuildNode(path, includeIgnored, currentDepth: 0, maxDepth);
        var directoryCount = CountDirectories(root) - 1;
        var fileCount = CountFiles(root);

        var sb = new StringBuilder();
        sb.AppendLine($"tree d={Math.Max(directoryCount, 0)} f={fileCount} depth={maxDepth}");
        sb.AppendLine($"{Path.GetFileName(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}/");

        foreach (var child in root.Directories)
            AppendTreeNode(sb, child, 1);

        if (root.Files.Count > 0)
            sb.AppendLine($"files={string.Join(",", root.Files.Take(topFiles).Select(Path.GetFileName))}");

        return sb.ToString();
    }

    public static async Task<string> RenderFilesAsync(string[] args, CliOptions cliOptions)
    {
        var path = FindPathArg(args) ?? ".";
        var flags = args;

        if (!Directory.Exists(path))
            return $"tk files: {path}: no such directory\n";

        var changedOnly = flags.Contains("--changed");
        var extension = ParseExtension(flags);
        var top = ParseTop(flags) ?? (cliOptions.Raw ? 50 : cliOptions.DetailLevel == DetailLevel.More ? 20 : 8);
        List<string> files;

        if (changedOnly)
        {
            files = await GetChangedFilesAsync(path);
        }
        else
        {
            files = EnumerateFiles(path, includeIgnored: cliOptions.Raw).ToList();
        }

        if (!string.IsNullOrEmpty(extension))
            files = files.Where(f => string.Equals(Path.GetExtension(f), extension, StringComparison.OrdinalIgnoreCase)).ToList();

        var relative = files
            .Select(f => MakeRelative(path, f))
            .OrderBy(ScoreFile)
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToList();

        var groups = relative
            .GroupBy(TopGroup)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(cliOptions.DetailLevel == DetailLevel.More ? 6 : 3)
            .Select(g => $"{g.Key}({g.Count()})")
            .ToList();

        var sb = new StringBuilder();
        sb.Append($"files n={relative.Count}");
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

        return sb.ToString();
    }

    private static DirectoryNode BuildNode(string path, bool includeIgnored, int currentDepth, int maxDepth)
    {
        var node = new DirectoryNode(path);

        var directories = Directory.GetDirectories(path)
            .Where(d => includeIgnored || !IgnoredDirectories.Contains(Path.GetFileName(d)))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in Directory.GetFiles(path).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            node.Files.Add(file);

        if (currentDepth >= maxDepth)
            return node;

        foreach (var directory in directories)
            node.Directories.Add(BuildNode(directory, includeIgnored, currentDepth + 1, maxDepth));

        return node;
    }

    private static void AppendTreeNode(StringBuilder sb, DirectoryNode node, int depth)
    {
        var indent = new string(' ', depth * 2);
        sb.AppendLine($"{indent}{Path.GetFileName(node.Path)}/ d={CountDirectories(node) - 1} f={CountFiles(node)}");
        foreach (var child in node.Directories)
            AppendTreeNode(sb, child, depth + 1);
    }

    private static int CountDirectories(DirectoryNode node) =>
        1 + node.Directories.Sum(CountDirectories);

    private static int CountFiles(DirectoryNode node) =>
        node.Files.Count + node.Directories.Sum(CountFiles);

    private static IEnumerable<string> EnumerateFiles(string path, bool includeIgnored)
    {
        foreach (var file in Directory.GetFiles(path))
            yield return file;

        foreach (var directory in Directory.GetDirectories(path))
        {
            if (!includeIgnored && IgnoredDirectories.Contains(Path.GetFileName(directory)))
                continue;

            foreach (var file in EnumerateFiles(directory, includeIgnored))
                yield return file;
        }
    }

    private static async Task<List<string>> GetChangedFilesAsync(string path)
    {
        try
        {
            var (exitCode, stdout, stderr) = await CommandRunner.RunAsync(["git", "-C", path, "status", "--porcelain=v1"]);
            if (exitCode != 0)
                return [];

            return Combine(stdout, stderr)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Length >= 4)
                .Select(line => Path.Combine(path, line[3..].Trim()))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string Combine(string stdout, string stderr) =>
        string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : $"{stdout.TrimEnd()}\n{stderr}";

    private static string MakeRelative(string basePath, string fullPath)
    {
        var relative = Path.GetRelativePath(basePath, fullPath);
        return PathUtils.StripPrefix(relative, "");
    }

    private static int ScoreFile(string path)
    {
        var fileName = Path.GetFileName(path);
        var depth = path.Count(c => c is '/' or '\\');
        var score = depth * 10 + path.Length;

        if (fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase)) score -= 30;
        if (fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)) score -= 25;
        if (fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) score -= 20;
        if (fileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase)) score -= 20;
        if (fileName.StartsWith("index.", StringComparison.OrdinalIgnoreCase)) score -= 12;
        if (fileName.StartsWith("main.", StringComparison.OrdinalIgnoreCase)) score -= 12;

        return score;
    }

    private static string TopGroup(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slash = normalized.IndexOf('/');
        return slash > 0 ? normalized[..slash] : "root";
    }

    private static int? ParseDepth(string[] flags)
    {
        for (var i = 0; i < flags.Length - 1; i++)
        {
            if (flags[i] == "--depth" && int.TryParse(flags[i + 1], out var depth))
                return Math.Max(1, depth);
        }

        return null;
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

    private sealed class DirectoryNode(string path)
    {
        public string Path { get; } = path;
        public List<DirectoryNode> Directories { get; } = [];
        public List<string> Files { get; } = [];
    }
}
