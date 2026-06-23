namespace Tk.Lsp;

/// <summary>
/// Formats LSP rename results into compact tk-style output.
/// </summary>
public static class RenameFormatter
{
    /// <summary>
    /// Formats rename results into a compact multi-line string.
    /// Header: rename &lt;symbol&gt; -> &lt;newName&gt; n=&lt;totalEdits&gt; f=&lt;files&gt;
    /// Then per file: file=&lt;localPath&gt; n=&lt;count&gt; with 1-based line:col lines.
    /// </summary>
    public static string Format(string symbol, string newName, IReadOnlyList<FileEdits> files)
    {
        var totalEdits = files.Sum(f => f.Edits.Length);

        if (files.Count == 0)
            return $"rename {symbol} -> {newName} n=0 f=0";

        var sb = new System.Text.StringBuilder();
        sb.Append($"rename {symbol} -> {newName} n={totalEdits} f={files.Count}");

        var sorted = files
            .OrderBy(f => f.Uri, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in sorted)
        {
            var filePath = UriToPath(file.Uri);
            var edits = file.Edits.OrderBy(e => e.StartLine).ThenBy(e => e.StartChar).ToList();
            sb.AppendLine();
            sb.Append($"  file={filePath} n={edits.Count}");
            foreach (var edit in edits)
            {
                sb.AppendLine();
                // LSP is 0-based; display as 1-based
                sb.Append($"    {edit.StartLine + 1}:{edit.StartChar + 1}");
            }
        }

        return sb.ToString();
    }

    internal static string UriToPath(string uri)
    {
        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try { return new Uri(uri).LocalPath; }
            catch { /* fall through */ }
        }
        return uri;
    }
}
