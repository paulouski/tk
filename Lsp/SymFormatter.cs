namespace Tk.Lsp;

/// <summary>
/// Formats workspace/symbol fuzzy search results (`tk sym`) into compact tk-style output:
/// grouped by file, capped at a top-N (with a hidden-count marker in tk house style, mirroring
/// DiagFormatter's `f=N/total (--more)`) since a broad fuzzy query can return hundreds of hits.
/// </summary>
public static class SymFormatter
{
    internal const int DefaultCap = 50;
    internal const int MoreCap = 300;

    public static string Format(string query, IReadOnlyList<SymbolMatch> matches, int cap = DefaultCap)
    {
        if (matches.Count == 0)
            return $"sym {query} n=0";

        var byFile = matches
            .GroupBy(m => m.Location.Uri)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalCount = matches.Count;
        var shownByFile = matches
            .Take(cap)
            .GroupBy(m => m.Location.Uri)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.Append($"sym {query} n={totalCount} f={byFile.Count}");
        if (totalCount > cap)
            sb.Append($" (top {cap}, --more)");

        foreach (var group in shownByFile)
        {
            var filePath = RefsFormatter.UriToPath(group.Key);
            var ordered = group.OrderBy(m => m.Location.StartLine).ToList();
            sb.AppendLine();
            sb.Append($"  file={filePath} n={ordered.Count}");
            foreach (var m in ordered)
            {
                sb.AppendLine();
                var label = string.IsNullOrEmpty(m.ContainerName) ? m.Name : $"{m.ContainerName}.{m.Name}";
                // LSP is 0-based; display as 1-based
                sb.Append($"    {m.Location.StartLine + 1}:{m.Location.StartChar + 1} {m.Kind} {label}");
            }
        }

        return sb.ToString();
    }
}
