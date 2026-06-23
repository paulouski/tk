namespace Tk.Lsp;

/// <summary>
/// Applies LSP TextEdits to file text, returning the updated text.
/// </summary>
public static class RenameEditApplier
{
    /// <summary>
    /// Applies non-overlapping LSP TextEdits to <paramref name="text"/>, returning the new text.
    /// LSP positions are 0-based (line, UTF-16 char). Edits are applied sorted by start position
    /// DESCENDING so earlier offsets remain valid as later edits are applied first.
    /// Computes absolute offsets via line-start scan (split on '\n').
    /// Throws <see cref="ArgumentOutOfRangeException"/> if any edit position is out of range.
    /// </summary>
    public static string Apply(string text, IReadOnlyList<RenameTextEdit> edits)
    {
        if (edits.Count == 0)
            return text;

        // Build line-start offset table. Split on '\n' but preserve '\r' in the line text
        // so that absolute offsets correctly account for \r\n line endings.
        var lineStarts = BuildLineStarts(text);

        // Sort descending by start position so we can apply from end to start
        var sorted = edits
            .OrderByDescending(e => e.StartLine)
            .ThenByDescending(e => e.StartChar)
            .ToList();

        var chars = new System.Text.StringBuilder(text);

        foreach (var edit in sorted)
        {
            var startOffset = GetOffset(lineStarts, text, edit.StartLine, edit.StartChar);
            var endOffset = GetOffset(lineStarts, text, edit.EndLine, edit.EndChar);

            if (startOffset < 0 || startOffset > text.Length)
                throw new ArgumentOutOfRangeException(nameof(edits),
                    $"Edit start position ({edit.StartLine},{edit.StartChar}) offset {startOffset} is out of range [0,{text.Length}].");
            if (endOffset < startOffset || endOffset > text.Length)
                throw new ArgumentOutOfRangeException(nameof(edits),
                    $"Edit end position ({edit.EndLine},{edit.EndChar}) offset {endOffset} is out of range [{startOffset},{text.Length}].");

            chars.Remove(startOffset, endOffset - startOffset);
            chars.Insert(startOffset, edit.NewText);

            // Update text for subsequent offset calculations against the modified content.
            // Since we apply descending, the portion BEFORE startOffset is unchanged; we only
            // need to update lineStarts for correctness if a later (earlier-in-file) edit
            // crosses the boundary we just modified. Rebuilding text and lineStarts each
            // iteration is the safest and correct approach for a low-volume apply.
            text = chars.ToString();
            lineStarts = BuildLineStarts(text);
        }

        return text;
    }

    private static int[] BuildLineStarts(string text)
    {
        var starts = new System.Collections.Generic.List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }
        return starts.ToArray();
    }

    private static int GetOffset(int[] lineStarts, string text, int line, int character)
    {
        if (line < 0 || line >= lineStarts.Length)
        {
            // LSP allows positions at the end of the last line (line == lineStarts.Length
            // when the file ends with '\n', pointing to the conceptual next line).
            // Treat that as the end of the file.
            if (line == lineStarts.Length)
                return text.Length;
            throw new ArgumentOutOfRangeException(nameof(line),
                $"Line {line} is out of range [0,{lineStarts.Length - 1}].");
        }

        var offset = lineStarts[line] + character;
        // Clamp to end of line (defensive; LSP clients may emit char == line length for EOL positions)
        var lineEnd = line + 1 < lineStarts.Length ? lineStarts[line + 1] : text.Length;
        if (offset > lineEnd)
            throw new ArgumentOutOfRangeException(nameof(character),
                $"Character {character} at line {line} exceeds line end offset {lineEnd - lineStarts[line]}.");
        return offset;
    }
}
