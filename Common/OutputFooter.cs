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
    /// <param name="extraHiddenCount">
    /// Additional hidden count known to the caller from outside the raw/filtered line diff
    /// (e.g. a fetch-time cap like <c>tk git log</c>'s injected <c>-10</c>, where the raw output
    /// never contained the rest of the history to begin with). Added to both the shown hidden
    /// count and the reported total so the footer stays truthful about what's missing.
    /// </param>
    public static string? Format(int originalLines, int shownLines, int unparsedCount,
        DetailLevel level, string? rawPath = null, int extraHiddenCount = 0)
    {
        var hidden = (originalLines > shownLines ? originalLines - shownLines : 0) + extraHiddenCount;
        var total = originalLines + extraHiddenCount;
        if (hidden <= 0 && unparsedCount <= 0 && rawPath is null)
            return null;

        var parts = new List<string>();
        if (hidden > 0)
            parts.Add($"hid={hidden}/{total}");
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
