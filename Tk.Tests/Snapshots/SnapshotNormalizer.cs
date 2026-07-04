using System.Text.RegularExpressions;

namespace Tk.Tests.Snapshots;

/// <summary>
/// Normalization applied identically when generating (TK_UPDATE_SNAPSHOTS=1) and when
/// comparing snapshot fixtures, so that inherently-volatile substrings (ANSI codes, absolute
/// temp paths, durations, hashes, ...) don't cause false-positive diffs while still letting
/// genuine behavior changes surface.
/// </summary>
public static partial class SnapshotNormalizer
{
    public static string Normalize(string text, bool isGitArea)
    {
        var s = text.Replace("\r\n", "\n").Replace("\r", "\n");
        s = AnsiRe().Replace(s, "");
        s = DurationRe().Replace(s, "t=<t>");
        s = TempPathRe().Replace(s, m => "/WORK/" + PathFileName(m.Value));
        s = PidRe().Replace(s, "pid=<pid>");

        if (isGitArea)
            s = NormalizeGitHashes(s);

        return s;
    }

    private static string PathFileName(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx >= 0 && idx < path.Length - 1 ? path[(idx + 1)..] : path;
    }

    /// <summary>Replaces every distinct git commit/blob hash (7-40 lowercase hex chars) with a
    /// stable placeholder keyed by first-seen order within this text (&lt;h1&gt;, &lt;h2&gt;, ...),
    /// so that hash VALUES don't cause diffs but hash IDENTITY changes (e.g. two previously-equal
    /// hashes becoming different) still surface.</summary>
    private static string NormalizeGitHashes(string s)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        return GitHashRe().Replace(s, m =>
        {
            if (!map.TryGetValue(m.Value, out var placeholder))
            {
                placeholder = $"<h{map.Count + 1}>";
                map[m.Value] = placeholder;
            }
            return placeholder;
        });
    }

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex AnsiRe();

    // Negative lookbehind for a word char excludes "st=" (staged count), "untr=" etc. — only
    // a standalone "t=" token (preceded by whitespace/line-start) is a duration field.
    [GeneratedRegex(@"(?<!\w)t=\S+")]
    private static partial Regex DurationRe();

    [GeneratedRegex(@"(?:/private)?(?:/Users/[^\s:'"")\]]+|/var/folders/[^\s:'"")\]]+|/tmp/[^\s:'"")\]]+)")]
    private static partial Regex TempPathRe();

    [GeneratedRegex(@"\bpid[:=]\s*\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex PidRe();

    [GeneratedRegex(@"\b[0-9a-f]{7,40}\b")]
    private static partial Regex GitHashRe();
}
