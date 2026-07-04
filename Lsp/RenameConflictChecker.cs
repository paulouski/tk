namespace Tk.Lsp;

/// <summary>
/// Pure decision logic for detecting a probable rename collision before edits are applied.
/// Roslyn's textDocument/rename computes valid edits for the symbol being renamed, but it does
/// not check whether the new name collides with an unrelated symbol already declared in the
/// same scope (e.g. renaming an interface to the name of another interface that already exists
/// in the same namespace) — that succeeds silently and leaves a broken build (CS0101 and
/// friends). These helpers correlate workspace/symbol results to catch that heuristically.
/// </summary>
public static class RenameConflictChecker
{
    /// <summary>
    /// Extracts the identifier (contiguous letters/digits/underscores) at or containing
    /// <paramref name="character"/> in <paramref name="lineText"/>. Returns "" if there is no
    /// identifier character at that position (out of range, or the position lands on
    /// punctuation/whitespace).
    /// </summary>
    public static string ExtractIdentifier(string lineText, int character)
    {
        if (string.IsNullOrEmpty(lineText) || character < 0 || character >= lineText.Length)
            return "";

        if (!IsIdentifierChar(lineText[character]))
            return "";

        var start = character;
        while (start > 0 && IsIdentifierChar(lineText[start - 1]))
            start--;

        var end = character;
        while (end < lineText.Length && IsIdentifierChar(lineText[end]))
            end++;

        return lineText[start..end];
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Picks the workspace/symbol match that corresponds to a symbol's own declaration, given
    /// the (uri, line) of its resolved definition. An exact uri+line match wins; if exactly one
    /// candidate shares the uri (e.g. a multi-line declaration signature shifts the line
    /// slightly), that one is accepted. Returns null if the declaration can't be identified
    /// reliably — no candidate shares the uri, or more than one does with no exact line match
    /// (e.g. a common member name declared many times in the same file).
    /// </summary>
    public static SymbolMatch? FindDeclarationMatch(
        IReadOnlyList<SymbolMatch> candidates, string definitionUri, int definitionLine)
    {
        foreach (var m in candidates)
        {
            if (m.Location.Uri == definitionUri && m.Location.StartLine == definitionLine)
                return m;
        }

        SymbolMatch? sameFileOnly = null;
        foreach (var m in candidates)
        {
            if (m.Location.Uri != definitionUri) continue;
            if (sameFileOnly is not null) return null; // more than one — ambiguous
            sameFileOnly = m;
        }

        return sameFileOnly;
    }

    /// <summary>
    /// Returns the first candidate among <paramref name="newNameCandidates"/> declared in the
    /// same container as <paramref name="oldContainer"/> — a probable rename collision, since a
    /// symbol with the target name already exists in that namespace/type. Returns null if no
    /// such candidate exists (rename is safe as far as this heuristic can tell).
    /// </summary>
    public static SymbolMatch? FindConflict(string oldContainer, IReadOnlyList<SymbolMatch> newNameCandidates)
    {
        foreach (var candidate in newNameCandidates)
        {
            if (string.Equals(candidate.ContainerName, oldContainer, StringComparison.Ordinal))
                return candidate;
        }
        return null;
    }
}
