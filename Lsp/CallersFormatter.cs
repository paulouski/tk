namespace Tk.Lsp;

/// <summary>
/// Formats LSP call-hierarchy incoming-calls results into compact tk-style output.
/// </summary>
public static class CallersFormatter
{
    /// <summary>
    /// Formats incoming callers into a compact multi-line string.
    /// Header: callers &lt;symbol&gt; n=&lt;totalCallSites&gt; f=&lt;distinctFiles&gt;
    /// Then grouped by call-site file, sorted by file then line.
    /// </summary>
    public static string Format(string symbol, IReadOnlyList<CallerInfo> callers)
    {
        // Collect all call sites: (file, line, char, callerName)
        var sites = new List<(string File, int Line, int Char, string CallerName)>();
        foreach (var caller in callers)
        {
            foreach (var site in caller.CallSites)
            {
                var file = RefsFormatter.UriToPath(site.Uri);
                sites.Add((file, site.StartLine, site.StartChar, caller.Name));
            }
        }

        if (sites.Count == 0)
            return $"callers {symbol} n=0 f=0";

        var byFile = sites
            .GroupBy(s => s.File)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalCount = sites.Count;
        var fileCount = byFile.Count;

        var sb = new System.Text.StringBuilder();
        sb.Append($"callers {symbol} n={totalCount} f={fileCount}");

        foreach (var group in byFile)
        {
            var ordered = group.OrderBy(s => s.Line).ThenBy(s => s.Char).ToList();
            sb.AppendLine();
            sb.Append($"  file={group.Key} n={ordered.Count}");
            foreach (var s in ordered)
            {
                sb.AppendLine();
                // LSP is 0-based; display as 1-based
                sb.Append($"    {s.Line + 1}:{s.Char + 1} {s.CallerName}");
            }
        }

        return sb.ToString();
    }
}
