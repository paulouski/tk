namespace Tk.Common;

/// <summary>
/// Age-based sweep of a state/scratch directory. Shared by the two places that write files
/// nobody ever deletes by hand: the raw-output store and the per-workspace daemon logs.
/// </summary>
public static class StaleFileSweeper
{
    /// <summary>
    /// Deletes files in <paramref name="dir"/> matching <paramref name="searchPattern"/> whose
    /// last-write time is older than <paramref name="maxAge"/>. Best-effort throughout: any
    /// failure (permission error, file removed by something else mid-sweep, unreadable
    /// directory) is swallowed per-file and then wholesale — a cleanup problem must never fail
    /// the command or daemon that triggered it.
    /// </summary>
    public static void Sweep(string dir, TimeSpan maxAge, string searchPattern = "*")
    {
        try
        {
            var cutoffUtc = DateTime.UtcNow - maxAge;
            foreach (var file in Directory.EnumerateFiles(dir, searchPattern))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                        File.Delete(file);
                }
                catch
                {
                    // Skip whatever couldn't be inspected/deleted and keep sweeping the rest.
                }
            }
        }
        catch
        {
            // e.g. the directory itself became unreadable mid-sweep, or does not exist.
        }
    }
}
