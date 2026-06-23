namespace Tk.Lsp;

/// <summary>
/// Formats LSP reference results into compact tk-style output.
/// </summary>
public static class RefsFormatter
{
    /// <summary>
    /// Formats references into a compact multi-line string.
    /// Header: refs &lt;symbol&gt; n=&lt;count&gt; f=&lt;files&gt;
    /// Then grouped by file, sorted by file path.
    /// </summary>
    public static string Format(string symbol, IReadOnlyList<LspLocation> locations)
    {
        if (locations.Count == 0)
            return $"refs {symbol} n=0 f=0";

        var byFile = locations
            .GroupBy(l => l.Uri)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.Append($"refs {symbol} n={locations.Count} f={byFile.Count}");

        foreach (var group in byFile)
        {
            var filePath = UriToPath(group.Key);
            var locs = group.OrderBy(l => l.StartLine).ThenBy(l => l.StartChar).ToList();
            sb.AppendLine();
            sb.Append($"  file={filePath} n={locs.Count}");
            foreach (var loc in locs)
            {
                sb.AppendLine();
                // LSP is 0-based; display as 1-based
                sb.Append($"    {loc.StartLine + 1}:{loc.StartChar + 1}");
            }
        }

        return sb.ToString();
    }

    private static string UriToPath(string uri)
    {
        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try { return new Uri(uri).LocalPath; }
            catch { /* fall through */ }
        }
        return uri;
    }
}
