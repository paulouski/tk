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

    // How many repo boundaries (see IsRepoBoundary) the extended upward walk in
    // FindWarmestTarget is allowed to cross past the nearest match while looking for a live
    // ancestor daemon, before giving up and falling back to FindTarget. Bounds the walk so a
    // directory tree with no ancestor .sln anywhere doesn't get scanned all the way to "/".
    private const int MaxBoundariesAboveNearest = 4;

    /// <summary>
    /// Same upward walk as <see cref="FindTarget"/>, but — unlike it — keeps walking past repo
    /// boundaries (capped, see <see cref="MaxBoundariesAboveNearest"/>) looking for an ANCESTOR
    /// .sln/.csproj that already has a live daemon running. This avoids spinning up a redundant
    /// cold daemon for a narrow sibling root (e.g. a sub-repo's own .sln) when a warm daemon for
    /// a wider umbrella workspace above it (e.g. a monorepo-family .sln living outside any repo's
    /// .git) is already indexed and covers this same file.
    ///
    /// Prefers the OUTERMOST live-daemon candidate found while walking up. Falls back to
    /// <see cref="FindTarget"/>'s exact result (nearest .sln/.csproj, stopping at the first repo
    /// boundary) when no ancestor candidate has a live daemon — the common case is unaffected.
    /// </summary>
    public static string? FindWarmestTarget(string startPath)
    {
        var directory = File.Exists(startPath) ? Path.GetDirectoryName(startPath)! : startPath;
        if (File.Exists(startPath) && (startPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                                       startPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            return startPath;
        }

        string? warmest = null;
        var boundariesCrossed = 0;
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            var candidate = FindSingleTargetIn(current.FullName);
            if (candidate is not null && HasLiveDaemon(candidate))
                warmest = candidate; // keep overwriting: last found while walking up = outermost

            if (IsRepoBoundary(current.FullName))
            {
                boundariesCrossed++;
                if (boundariesCrossed > MaxBoundariesAboveNearest)
                    break;
            }

            current = current.Parent;
        }

        return warmest ?? FindTarget(startPath);
    }

    /// <summary>Unambiguous .sln/.csproj match in a single directory (no upward walk).</summary>
    private static string? FindSingleTargetIn(string directory)
    {
        var projects = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (projects.Length == 1)
            return projects[0];

        var solutions = Directory.EnumerateFiles(directory, "*.sln", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return solutions.Length == 1 ? solutions[0] : null;
    }

    private static bool HasLiveDaemon(string target)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var pidInfo = Lsp.DaemonSocket.TryReadPidInfo(Lsp.DaemonSocket.GetPidPath(root));
        return pidInfo is not null && Lsp.DaemonSocket.IsProcessAlive(pidInfo.DaemonPid);
    }
}
