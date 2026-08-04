using System.Security.Cryptography;
using System.Text;

namespace Tk.Common;

public static class RawOutputStore
{
    /// <summary>Raw copies older than this are purged on every store write.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>
    /// Saves a copy of the raw pre-filter output and returns its path, per the exit-code policy
    /// in docs/output-contract.md: a raw copy is offered when the command failed OR the filter
    /// found unparsed (alien) content — either way the agent may want to see what the filter
    /// didn't (fully) understand. Returns null when neither applies, or when filtering was a
    /// no-op (raw == filtered, e.g. PassthroughFilter), since there is nothing extra to show.
    /// </summary>
    public static string? SaveIfNeeded(string raw, string filteredBeforeFooter, string[] commandArgs,
        int exitCode, int unparsedCount)
    {
        if (exitCode == 0 && unparsedCount <= 0)
            return null;
        if (string.IsNullOrWhiteSpace(raw) || raw == filteredBeforeFooter)
            return null;

        return Save(raw, commandArgs);
    }

    /// <summary>
    /// Directory the store writes into: <c>$TK_RAW_DIR</c> when set (deterministic override for
    /// tests), otherwise <c>&lt;temp&gt;/tk-raw</c>.
    /// </summary>
    public static string ResolveDir()
    {
        var overrideDir = Environment.GetEnvironmentVariable("TK_RAW_DIR");
        return !string.IsNullOrWhiteSpace(overrideDir)
            ? overrideDir
            : Path.Combine(Path.GetTempPath(), "tk-raw");
    }

    private static string Save(string raw, string[] commandArgs)
    {
        var dir = ResolveDir();
        Directory.CreateDirectory(dir);

        // Sweep stale files before writing this run's own file, so the new file is never a
        // candidate (it doesn't exist yet) and no ordering trick is needed to protect it.
        StaleFileSweeper.Sweep(dir, MaxAge);

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        var command = commandArgs.Length == 0 ? "cmd" : commandArgs[0];
        var safeCommand = new string(command.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', commandArgs)))).ToLowerInvariant()[..8];
        var path = Path.Combine(dir, $"{stamp}-{safeCommand}-{hash}.log");

        File.WriteAllText(path, raw);
        return path;
    }

}
