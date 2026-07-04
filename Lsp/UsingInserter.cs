using System.Text.RegularExpressions;

namespace Tk.Lsp;

/// <summary>
/// Pure helper for adding a `using` directive to a C# file if it isn't already present. Used by
/// <c>tk mv --fix-ns</c> to patch files that reference the moved file's types once the compiler
/// (via <c>diag</c>) reports they can no longer resolve them under the new namespace. Text-based
/// on purpose, mirroring <see cref="NamespaceConvention"/>: a using directive is a mechanical,
/// single-line construct.
/// </summary>
public static class UsingInserter
{
    // Top-level "using Name.Space;" directives only — excludes `using static` (a different kind
    // of directive) and aliased usings (`using X = Y;`, which wouldn't match `Name.Space` as the
    // whole captured group anyway since '=' isn't part of the identifier pattern).
    private static readonly Regex UsingLineRegex = new(
        @"^using[ \t]+(?!static\b)(?<name>[A-Za-z_][A-Za-z0-9_.]*)[ \t]*;",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Returns <paramref name="fileText"/> unchanged if a top-level `using {namespaceName};`
    /// directive is already present. Otherwise inserts a new `using {namespaceName};` line
    /// immediately after the last existing top-level `using` directive, or at the very start of
    /// the file if there are none.
    /// </summary>
    public static string AddUsingIfMissing(string fileText, string namespaceName)
    {
        var matches = UsingLineRegex.Matches(fileText);
        foreach (Match m in matches)
        {
            if (m.Groups["name"].Value == namespaceName)
                return fileText;
        }

        var newline = fileText.Contains("\r\n") ? "\r\n" : "\n";
        var directive = $"using {namespaceName};{newline}";

        if (matches.Count == 0)
            return directive + fileText;

        var last = matches[^1];
        var insertAt = last.Index + last.Length;
        if (insertAt < fileText.Length && fileText[insertAt] == '\r') insertAt++;
        if (insertAt < fileText.Length && fileText[insertAt] == '\n') insertAt++;

        return fileText[..insertAt] + directive + fileText[insertAt..];
    }
}
