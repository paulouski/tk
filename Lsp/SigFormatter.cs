namespace Tk.Lsp;

/// <summary>
/// Formats LSP textDocument/hover results into a compact tk-style block: signature, return
/// type, and doc-comment summary. Roslyn's hover text is markdown — a fenced code block
/// (```csharp ... ```) around the signature, followed by plain-text doc-comment prose — this
/// strips the fence markers and collapses blank-line runs, leaving just that content.
/// </summary>
public static class SigFormatter
{
    public static string Format(string symbol, HoverResult? hover)
    {
        if (hover is null || string.IsNullOrWhiteSpace(hover.Contents))
            return $"sig {symbol}: no hover info";

        var path = RefsFormatter.UriToPath(hover.Uri);
        var line1 = hover.Line + 1;
        var col1 = hover.Character + 1;
        var body = StripMarkdownNoise(hover.Contents);

        var sb = new System.Text.StringBuilder();
        sb.Append($"sig {symbol}  {path}:{line1}:{col1}");
        foreach (var line in body.Split('\n'))
        {
            sb.AppendLine();
            sb.Append($"  {line.TrimEnd()}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Strips markdown fence markers (```lang / ```) and collapses runs of blank lines and
    /// leading/trailing blanks, leaving the plain-text signature + summary Roslyn wraps in them.
    /// </summary>
    internal static string StripMarkdownNoise(string markdown)
    {
        var lines = markdown
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("```", StringComparison.Ordinal))
            .ToList();

        var collapsed = new List<string>();
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0 && collapsed.Count > 0 && collapsed[^1].Trim().Length == 0)
                continue;
            collapsed.Add(line);
        }

        while (collapsed.Count > 0 && collapsed[0].Trim().Length == 0) collapsed.RemoveAt(0);
        while (collapsed.Count > 0 && collapsed[^1].Trim().Length == 0) collapsed.RemoveAt(collapsed.Count - 1);

        return string.Join("\n", collapsed);
    }
}
