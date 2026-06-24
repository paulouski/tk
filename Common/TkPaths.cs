namespace Tk.Common;

/// <summary>
/// Resolves the on-disk locations tk uses for its own runtime/state data
/// (analytics logs, daemon sockets and logs).
///
/// Root follows the XDG Base Directory spec for state data: honours
/// <c>$XDG_STATE_HOME</c> when set, otherwise <c>~/.local/state</c>. All tk data
/// lives under a single <c>tk/</c> subtree so nothing leaks into <c>~/.claude/</c>.
/// </summary>
public static class TkPaths
{
    /// <summary>Root for all tk state data: <c>$XDG_STATE_HOME/tk</c> or <c>~/.local/state/tk</c>.</summary>
    public static string Root()
    {
        var xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var baseDir = !string.IsNullOrEmpty(xdgState)
            ? xdgState
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "state");
        return Path.Combine(baseDir, "tk");
    }

    /// <summary>Append-only analytics logs directory.</summary>
    public static string AnalyticsDir() => Path.Combine(Root(), "analytics");

    /// <summary>Per-workspace daemon sockets and logs directory.</summary>
    public static string DaemonsDir() => Path.Combine(Root(), "daemons");

    /// <summary>Module-enablement config file (one module name per line).</summary>
    public static string ModulesFile() => Path.Combine(Root(), "modules");
}
