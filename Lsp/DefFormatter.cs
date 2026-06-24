namespace Tk.Lsp;

/// <summary>
/// Formats LSP go-to-definition results into compact tk-style output.
///
/// The readable-file branch transparently handles BOTH in-workspace source AND any real
/// temp file Roslyn may write for metadata-as-source/decompiled definitions — no
/// special-casing needed; if Roslyn returns a real temp file path it just works.
/// Non-file URIs or paths that do not exist on disk are shown in a degraded form so
/// the maintainer can observe exactly what URI Roslyn returns for external symbols.
/// </summary>
public static class DefFormatter
{
    private const int DefWindowLines = 30;

    public static string Format(string symbol, IReadOnlyList<LspLocation> locations)
    {
        if (locations.Count == 0)
            return $"def {symbol} n=0";

        var sb = new System.Text.StringBuilder();
        sb.Append($"def {symbol} n={locations.Count}");

        foreach (var loc in locations)
        {
            var path = RefsFormatter.UriToPath(loc.Uri);
            var line1 = loc.StartLine + 1;
            var col1 = loc.StartChar + 1;

            sb.AppendLine();

            if (File.Exists(path))
            {
                sb.Append($"  file={path}:{line1}:{col1}");

                string[] lines;
                try { lines = File.ReadAllLines(path); }
                catch { lines = []; }

                if (lines.Length > 0)
                {
                    var windowStart = loc.StartLine; // 0-based index
                    var windowEnd = Math.Min(lines.Length - 1, windowStart + DefWindowLines - 1);
                    for (var i = windowStart; i <= windowEnd; i++)
                    {
                        // Roslyn appends a verbose "#if false // Decompilation log ... #endif" block to
                        // metadata-as-source files. Stop before it — the signature above is the signal.
                        if (lines[i].TrimStart().StartsWith("#if false", StringComparison.Ordinal)
                            && lines[i].Contains("Decompilation log", StringComparison.Ordinal))
                            break;

                        var content = Truncate(lines[i], 180);
                        sb.AppendLine();
                        sb.Append($"    {i + 1,4}| {content}");
                    }
                }
            }
            else
            {
                sb.Append($"  uri={loc.Uri}:{line1}:{col1} (external — not readable)");
            }
        }

        return sb.ToString();
    }

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "..." : s;
}
