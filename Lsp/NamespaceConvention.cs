using System.Text;
using System.Text.RegularExpressions;

namespace Tk.Lsp;

/// <summary>
/// A file's top-level namespace declaration, as found by <see cref="NamespaceConvention.ParseDeclaration"/>.
/// <see cref="Start"/>/<see cref="Length"/> span only the namespace name text itself (e.g. the
/// "Foo.Bar" in "namespace Foo.Bar;"), not the "namespace" keyword or the trailing `;`/`{`.
/// </summary>
public readonly record struct NamespaceDeclaration(string Name, bool FileScoped, int Start, int Length);

/// <summary>
/// Pure helpers for computing a C# file's expected namespace from its path (the IDE
/// folder-to-namespace convention) and for rewriting a file's namespace declaration text,
/// used by <c>tk mv --fix-ns</c>. Text-based on purpose (see docs/lsp-daemon-architecture.md):
/// namespace declarations are a mechanical, single-line construct, so a targeted text edit is
/// simpler and safer here than round-tripping through the LSP daemon.
/// </summary>
public static class NamespaceConvention
{
    private static readonly Regex RootNamespaceRegex = new(
        @"<RootNamespace>\s*([^<\s][^<]*?)\s*</RootNamespace>", RegexOptions.Compiled);

    // Matches the first top-level namespace declaration: "namespace Name;" (file-scoped) or
    // "namespace Name {" (block-scoped). Anchored to the start of a line (ignoring leading
    // whitespace) since that's how every realistic namespace declaration is written.
    private static readonly Regex NamespaceDeclRegex = new(
        @"^[ \t]*namespace[ \t]+([A-Za-z_][A-Za-z0-9_.]*)\s*(;|\{)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Resolves the <c>&lt;RootNamespace&gt;</c> element from a .csproj file's raw XML text, or
    /// the project file's name (without extension) if the element is absent — mirrors MSBuild's
    /// own default (<c>RootNamespace</c> defaults to <c>MSBuildProjectName</c>).
    /// </summary>
    public static string ResolveRootNamespace(string csprojText, string csprojPath)
    {
        var match = RootNamespaceRegex.Match(csprojText);
        if (match.Success)
            return match.Groups[1].Value;

        return Path.GetFileNameWithoutExtension(csprojPath);
    }

    /// <summary>
    /// Walks up from <paramref name="startDir"/> looking for the nearest ancestor directory
    /// that directly contains a *.csproj file, returning that .csproj's full path. Returns null
    /// if none is found before reaching the filesystem root.
    /// </summary>
    public static string? FindOwningProject(string startDir)
    {
        var dir = Path.GetFullPath(startDir);
        while (true)
        {
            var csproj = Directory.EnumerateFiles(dir, "*.csproj").FirstOrDefault();
            if (csproj is not null)
                return csproj;

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir)
                return null;
            dir = parent;
        }
    }

    /// <summary>
    /// Computes the expected namespace for <paramref name="filePath"/> under a project rooted at
    /// <paramref name="projectDir"/> with the given <paramref name="rootNamespace"/>: the root
    /// namespace plus one dotted segment per directory between the project directory and the
    /// file's own directory — the standard IDE folder-to-namespace convention. A file directly
    /// in the project directory has no extra segments (namespace == rootNamespace). Segments
    /// that aren't valid C# identifier fragments (e.g. containing '-' or starting with a digit)
    /// are sanitized the way Visual Studio sanitizes folder names into namespace segments.
    /// </summary>
    public static string ComputeExpectedNamespace(string rootNamespace, string projectDir, string filePath)
    {
        var fileDir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? "";
        var relative = Path.GetRelativePath(Path.GetFullPath(projectDir), fileDir);

        if (relative == "." || string.IsNullOrEmpty(relative))
            return rootNamespace;

        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(s => s.Length > 0 && s != ".")
            .Select(SanitizeSegment)
            .ToArray();

        return segments.Length == 0
            ? rootNamespace
            : rootNamespace + "." + string.Join('.', segments);
    }

    private static string SanitizeSegment(string segment)
    {
        var sb = new StringBuilder(segment.Length);
        foreach (var c in segment)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

        if (sb.Length == 0)
            return "_";
        if (char.IsDigit(sb[0]))
            sb.Insert(0, '_');
        return sb.ToString();
    }

    /// <summary>
    /// Extracts the first top-level namespace declaration from C# source text: either
    /// file-scoped (<c>namespace X;</c>) or block-scoped (<c>namespace X { ... }</c>). Returns
    /// null when no namespace declaration is found (e.g. a global-namespace file).
    /// </summary>
    public static NamespaceDeclaration? ParseDeclaration(string fileText)
    {
        var match = NamespaceDeclRegex.Match(fileText);
        if (!match.Success)
            return null;

        var nameGroup = match.Groups[1];
        var fileScoped = match.Groups[2].Value == ";";
        return new NamespaceDeclaration(nameGroup.Value, fileScoped, nameGroup.Index, nameGroup.Length);
    }

    /// <summary>
    /// Rewrites the file's namespace declaration name to <paramref name="newNamespace"/>,
    /// preserving everything else byte-for-byte (the <c>;</c> or <c>{ ... }</c> body, comments,
    /// blank lines, usings). Throws <see cref="InvalidOperationException"/> if the file has no
    /// top-level namespace declaration.
    /// </summary>
    public static string RewriteDeclaration(string fileText, string newNamespace)
    {
        var decl = ParseDeclaration(fileText)
            ?? throw new InvalidOperationException("no top-level namespace declaration found");

        return fileText[..decl.Start] + newNamespace + fileText[(decl.Start + decl.Length)..];
    }
}
