using System.Text;
using Tk.Common;

namespace Tk.Commands;

public sealed class TreeCommand : ICommand
{
    public string Name => "tree";

    public Task<int> RunAsync(CommandContext ctx)
    {
        ctx.Out.Write(Render(ctx.Args, ctx.Raw, ctx.DetailLevel));
        return Task.FromResult(0);
    }

    private static string Render(string[] args, bool raw, DetailLevel detail)
    {
        var path = args.FirstOrDefault(a => !a.StartsWith('-')) ?? ".";
        var flags = args;

        if (!Directory.Exists(path))
            return $"tk tree: {path}: no such directory\n";

        var codeFocused = flags.Contains("--code");
        var includeIgnored = raw || flags.Contains("--all");
        var maxDepth = ParseDepth(flags) ?? (raw ? 5 : detail == DetailLevel.More ? 3 : 2);
        var topFiles = raw ? 12 : detail == DetailLevel.More ? 8 : 5;
        var root = BuildNode(path, includeIgnored, codeFocused, currentDepth: 0, maxDepth);
        var directoryCount = CountDirectories(root) - 1;
        var fileCount = CountFiles(root);

        var sb = new StringBuilder();
        sb.AppendLine(codeFocused
            ? $"tree code d={Math.Max(directoryCount, 0)} f={fileCount} depth={maxDepth}"
            : $"tree d={Math.Max(directoryCount, 0)} f={fileCount} depth={maxDepth}");
        sb.AppendLine($"{Path.GetFileName(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}/");

        foreach (var child in root.Directories)
            AppendTreeNode(sb, child, 1);

        if (root.Files.Count > 0)
            sb.AppendLine($"files={string.Join(",", root.Files.Take(topFiles).Select(Path.GetFileName))}");

        return sb.ToString();
    }

    private static DirectoryNode BuildNode(string path, bool includeIgnored, bool codeFocused, int currentDepth, int maxDepth)
    {
        var node = new DirectoryNode(path);

        var directories = Directory.GetDirectories(path)
            .Where(d => RepoScope.ShouldIncludeDirectory(d, includeIgnored, codeFocused))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in Directory.GetFiles(path)
            .Where(f => RepoScope.ShouldIncludeFile(f, codeFocused))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            node.Files.Add(file);
        }

        if (currentDepth >= maxDepth)
            return node;

        foreach (var directory in directories)
        {
            var child = BuildNode(directory, includeIgnored, codeFocused, currentDepth + 1, maxDepth);
            if (!codeFocused || child.Files.Count > 0 || child.Directories.Count > 0)
                node.Directories.Add(child);
        }

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

    private static int? ParseDepth(string[] flags)
    {
        for (var i = 0; i < flags.Length - 1; i++)
        {
            if (flags[i] == "--depth" && int.TryParse(flags[i + 1], out var depth))
                return Math.Max(1, depth);
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
