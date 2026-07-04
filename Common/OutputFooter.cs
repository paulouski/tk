namespace Tk.Common;

/// <summary>
/// Shared footer renderer (see docs/output-contract.md). Emits, in fixed order:
/// <c>hid=&lt;hidden&gt;/&lt;total&gt;</c> (semantics unchanged from the original
/// <see cref="HiddenLinesFooter"/>), then <c>unparsed=&lt;n&gt;</c> (only when &gt; 0), then
/// <c>raw=&lt;path&gt;</c> (when a raw copy/reference exists), then the escalation hint —
/// exactly as <see cref="HiddenLinesFooter"/> did before it.
/// </summary>
public static class OutputFooter
{
    public static string? Format(int originalLines, int shownLines, int unparsedCount,
        DetailLevel level, string? rawPath = null)
    {
        var hidden = originalLines > shownLines ? originalLines - shownLines : 0;
        if (hidden <= 0 && unparsedCount <= 0 && rawPath is null)
            return null;

        var parts = new List<string>();
        if (hidden > 0)
            parts.Add($"hid={hidden}/{originalLines}");
        if (unparsedCount > 0)
            parts.Add($"unparsed={unparsedCount}");
        if (rawPath is not null)
            parts.Add($"raw={rawPath}");

        if (parts.Count == 0)
            return null;

        var hint = level == DetailLevel.More ? "(--raw)" : "(--more, --raw)";
        return $"{string.Join(' ', parts)} {hint}";
    }

    public static int CountLines(string text) => HiddenLinesFooter.CountLines(text);
}
