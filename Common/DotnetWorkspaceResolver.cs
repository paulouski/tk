namespace Tk.Common;

/// <summary>
/// Shared resolver for finding the nearest .sln or .csproj from a starting path.
/// </summary>
public static class DotnetWorkspaceResolver
{
    /// <summary>
    /// Walks up from <paramref name="startPath"/> to find the nearest .sln or .csproj.
    /// Returns the absolute path or null if none found.
    /// </summary>
    public static string? FindTarget(string startPath)
    {
        var directory = File.Exists(startPath) ? Path.GetDirectoryName(startPath)! : startPath;
        if (File.Exists(startPath) && (startPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                                       startPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            return startPath;
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
}
