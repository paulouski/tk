using System.Text.RegularExpressions;

namespace Tk.Commands.View;

/// <summary>
/// Regex-backed outline provider — the universal fallback. Probes the same line-by-line
/// patterns the original <c>tk view</c> summary used (C# type/method/property, Python def,
/// JS function/arrow, markdown headings) and reuses the bracket/brace-aware "end at
/// self-terminator or next symbol" logic to compute approximate body ranges. Always
/// returns a non-null result for any text file (handles <c>.cs</c>, <c>.py</c>, <c>.js</c>,
/// <c>.ts</c>, <c>.md</c>, and anything else the regex set covers); the caller should
/// pre-filter to text files (the view command already does via <c>LooksBinary</c>).
///
/// Marked <c>source=regex approx</c> in the output so the user knows the ranges are
/// approximate, not LSP-grade.
/// </summary>
internal sealed partial class RegexOutlineProvider : IFileOutlineProvider
{
    public bool CanHandle(string path) => true;

    public Task<OutlineResult?> GetOutlineAsync(string path, CancellationToken ct)
    {
        // Cancellation is checked between phases; the regex scan is pure-CPU and very
        // fast (single pass per file), so the granularity is coarse but sufficient.
        ct.ThrowIfCancellationRequested();

        var lines = File.ReadAllLines(path);
        var symbols = ExtractSymbols(lines).ToList();
        var ranges = BuildHotRanges(symbols, lines).ToList();

        ct.ThrowIfCancellationRequested();

        var entries = new List<OutlineEntry>(ranges.Count);
        foreach (var r in ranges)
        {
            var bodySize = r.End - r.Start + 1;
            entries.Add(new OutlineEntry(
                Kind: r.Kind,
                Name: r.Name,
                StartLine: r.Start,
                EndLine: r.End,
                BodySize: bodySize,
                Detail: null,
                Children: []));
        }

        return Task.FromResult<OutlineResult?>(new OutlineResult("regex approx", entries));
    }

    // ─── ported from ViewCommand.cs ───────────────────────────────────────────

    private enum SymbolKind
    {
        Other,
        Type,
        Method,
        Property,
        JsArrow
    }

    private sealed record Symbol(string Name, int Line, SymbolKind Kind);
    private sealed record HotRange(string Name, int Start, int End, string Kind);

    private static IEnumerable<Symbol> ExtractSymbols(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var symbol = TryMatchSymbol(line, i + 1);
            if (symbol != null)
                yield return symbol;
        }
    }

    private static Symbol? TryMatchSymbol(string line, int lineNumber)
    {
        var markdown = MarkdownHeadingRe().Match(line);
        if (markdown.Success)
            return new Symbol(markdown.Groups["name"].Value.Trim(), lineNumber, SymbolKind.Other);

        var csType = TypeDeclRe().Match(line);
        if (csType.Success)
            return new Symbol(csType.Groups["name"].Value, lineNumber, SymbolKind.Type);

        var csMethod = MethodDeclRe().Match(line);
        if (csMethod.Success)
            return new Symbol(csMethod.Groups["name"].Value, lineNumber, SymbolKind.Method);

        var csProperty = PropertyDeclRe().Match(line);
        if (csProperty.Success)
            return new Symbol(csProperty.Groups["name"].Value, lineNumber, SymbolKind.Property);

        var pyDef = PythonDeclRe().Match(line);
        if (pyDef.Success)
            return new Symbol(pyDef.Groups["name"].Value, lineNumber, SymbolKind.Other);

        var jsFunc = JsFunctionRe().Match(line);
        if (jsFunc.Success)
            return new Symbol(jsFunc.Groups["name"].Value, lineNumber, SymbolKind.Other);

        var jsConst = JsConstFunctionRe().Match(line);
        if (jsConst.Success)
            return new Symbol(jsConst.Groups["name"].Value, lineNumber, SymbolKind.JsArrow);

        return null;
    }

    private static IEnumerable<HotRange> BuildHotRanges(List<Symbol> symbols, string[] lines)
    {
        var totalLines = lines.Length;
        for (var i = 0; i < symbols.Count; i++)
        {
            var current = symbols[i];
            var nextLine = i + 1 < symbols.Count ? symbols[i + 1].Line : totalLines + 1;
            var fallbackEnd = Math.Min(Math.Max(current.Line, nextLine - 1), totalLines);
            var end = fallbackEnd;

            if (IsSelfTerminating(current, lines))
                end = Math.Min(Math.Max(ComputeSelfTerminatingEnd(lines, current.Line, fallbackEnd), current.Line), totalLines);

            yield return new HotRange(current.Name, current.Line, end, ToKindLabel(current.Kind));
        }
    }

    private static string ToKindLabel(SymbolKind kind) => kind switch
    {
        SymbolKind.Type => "type",
        SymbolKind.Method => "method",
        SymbolKind.Property => "property",
        SymbolKind.JsArrow => "function",
        _ => "symbol",
    };

    /// <summary>
    /// Expression-bodied members (<c>=&gt; ...;</c>) and arrow functions don't end where the
    /// next symbol starts — they end at their own terminator. Properties are always short
    /// enough that computing their real end is cheap and safer than guessing via the next symbol.
    /// </summary>
    private static bool IsSelfTerminating(Symbol symbol, string[] lines)
    {
        return symbol.Kind switch
        {
            SymbolKind.Property or SymbolKind.JsArrow => true,
            SymbolKind.Method => lines[symbol.Line - 1].Contains("=>", StringComparison.Ordinal),
            _ => false
        };
    }

    /// <summary>
    /// Scans forward from a declaration line to find where its statement actually ends:
    /// the first top-level ';' for an expression body (e.g. <c>=&gt; expr;</c>), or the
    /// matching '}' for the first top-level block opened (e.g. <c>{ ... }</c> or arrow
    /// function bodies). Tracks paren/bracket/brace nesting and skips string/char literal
    /// contents so embedded punctuation doesn't confuse the scan. Falls back to
    /// <paramref name="fallbackEnd"/> if no terminator is found before end of file.
    /// </summary>
    private static int ComputeSelfTerminatingEnd(string[] lines, int startLine, int fallbackEnd)
    {
        var depth = 0;
        var inBlock = false;
        var inString = false;
        var inChar = false;
        var escaped = false;

        for (var lineIdx = startLine - 1; lineIdx < lines.Length; lineIdx++)
        {
            var line = lines[lineIdx];
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (inChar)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '\'') inChar = false;
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        continue;
                    case '\'':
                        inChar = true;
                        continue;
                    case '(':
                    case '[':
                        depth++;
                        continue;
                    case ')':
                    case ']':
                        depth = Math.Max(0, depth - 1);
                        continue;
                    case '{':
                        if (depth == 0 && !inBlock)
                            inBlock = true;
                        depth++;
                        continue;
                    case '}':
                        depth = Math.Max(0, depth - 1);
                        if (inBlock && depth == 0)
                            return lineIdx + 1;
                        continue;
                    case ';':
                        if (depth == 0 && !inBlock)
                            return lineIdx + 1;
                        continue;
                }
            }
        }

        return fallbackEnd;
    }

    [GeneratedRegex(@"^#{1,6}\s+(?<name>.+)$")]
    private static partial Regex MarkdownHeadingRe();

    [GeneratedRegex(@"\b(class|interface|record|struct|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex TypeDeclRe();

    [GeneratedRegex(@"^(?:public|private|protected|internal|static|sealed|partial|async|virtual|override|abstract|extern|new|\s)+[\w<>\[\],?.]+\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex MethodDeclRe();

    [GeneratedRegex(@"^(?:\[[^\]]*\]\s*)*(?:public|private|protected|internal|static|sealed|partial|readonly|virtual|override|abstract|new|required|\s)+[\w<>\[\],?.]+\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:\{|=>)")]
    private static partial Regex PropertyDeclRe();

    [GeneratedRegex(@"^(?:async\s+)?(?:def|class)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex PythonDeclRe();

    [GeneratedRegex(@"^(?:export\s+)?(?:async\s+)?function\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex JsFunctionRe();

    [GeneratedRegex(@"^(?:export\s+)?(?:const|let|var)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?:async\s*)?\(")]
    private static partial Regex JsConstFunctionRe();
}
