namespace Tk.Lsp;

/// <summary>
/// Formats LSP textDocument/implementation results into compact tk-style output.
/// </summary>
public static class ImplFormatter
{
    /// <summary>
    /// Formats implementation locations into a compact multi-line string.
    /// Header: impl &lt;symbol&gt; n=&lt;count&gt; f=&lt;files&gt;
    /// Then grouped by file (sorted), each location shown with a one-line source preview
    /// (best-effort — omitted if the file isn't readable).
    /// </summary>
    public static string Format(string symbol, IReadOnlyList<LspLocation> locations)
    {
        if (locations.Count == 0)
            return $"impl {symbol} n=0";

        var byFile = locations
            .GroupBy(l => l.Uri)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.Append($"impl {symbol} n={locations.Count} f={byFile.Count}");

        foreach (var group in byFile)
        {
            var filePath = RefsFormatter.UriToPath(group.Key);
            var locs = group.OrderBy(l => l.StartLine).ThenBy(l => l.StartChar).ToList();

            string[]? lines = null;
            if (File.Exists(filePath))
            {
                try { lines = File.ReadAllLines(filePath); }
                catch { lines = null; }
            }

            sb.AppendLine();
            sb.Append($"  file={filePath} n={locs.Count}");
            foreach (var loc in locs)
            {
                sb.AppendLine();
                var text = lines is not null && loc.StartLine >= 0 && loc.StartLine < lines.Length
                    ? Truncate(lines[loc.StartLine].Trim(), 140)
                    : "";
                // LSP is 0-based; display as 1-based
                sb.Append($"    {loc.StartLine + 1}:{loc.StartChar + 1} {text}");
            }
        }

        return sb.ToString();
    }

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "..." : s;
}
