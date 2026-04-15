namespace Tk;

public static class RepoScope
{
    public static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".idea", ".vs", "dist", "coverage"
    };

    private static readonly HashSet<string> CodeHiddenDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "docs", "_local", "assets", "public", "locales", "img", "images", "fonts", "coverage"
    };

    public static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".props", ".targets", ".fs", ".vb",
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
        ".json", ".yml", ".yaml", ".xml", ".sql", ".ps1", ".sh", ".bicep"
    };

    public static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".adoc", ".rst"
    };

    public static bool ShouldIncludeDirectory(string directory, bool includeIgnored, bool codeFocused)
    {
        var name = Path.GetFileName(directory);
        if (!includeIgnored && IgnoredDirectories.Contains(name))
            return false;

        if (codeFocused && CodeHiddenDirectories.Contains(name))
            return false;

        return true;
    }

    public static bool ShouldIncludeFile(string path, bool codeFocused)
    {
        if (!codeFocused)
            return true;

        return IsCodeFile(path);
    }

    public static bool IsCodeFile(string path)
    {
        if (IsGeneratedFile(path))
            return false;

        return CodeExtensions.Contains(Path.GetExtension(path));
    }

    public static bool IsDocFile(string path)
    {
        var extension = Path.GetExtension(path);
        if (DocExtensions.Contains(extension))
            return true;

        return Path.GetFileName(path).Equals("README.md", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGeneratedFile(string path)
    {
        var normalized = path.Replace('\\', '/');
        var name = Path.GetFileName(path);

        if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase);
    }

    public static int ScoreFile(string path, bool codeFocused)
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

        if (codeFocused)
        {
            if (fileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase)) score -= 25;
            if (fileName.Equals("Startup.cs", StringComparison.OrdinalIgnoreCase)) score -= 20;
            if (fileName.Equals("ServiceCollectionExtensions.cs", StringComparison.OrdinalIgnoreCase)) score -= 18;
            if (fileName.EndsWith("Endpoint.cs", StringComparison.OrdinalIgnoreCase)) score -= 16;
            if (fileName.EndsWith("EndpointExtension.cs", StringComparison.OrdinalIgnoreCase)) score -= 14;
            if (fileName.EndsWith("Handler.cs", StringComparison.OrdinalIgnoreCase)) score -= 12;
            if (fileName.StartsWith("I", StringComparison.Ordinal) && fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) score -= 6;
        }

        return score;
    }
}
