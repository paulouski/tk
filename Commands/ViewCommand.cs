using System.Text;
using System.Text.RegularExpressions;
using Tk.Common;

namespace Tk.Commands;

public sealed partial class ViewCommand : ICommand
{
    public string Name => "view";

    private const int SmallFileLineLimit = 40;
    private const int SmallFileCharLimit = 3000;
    private const int MaxSymbols = 8;
    private const int MaxSymbolsMore = 24;
    private const int MaxHotRanges = 5;
    private const int MaxHotRangesMore = 15;
    private const int MarkdownLineLimit = 200;
    private const int MarkdownCharLimit = 12000;
    private const int MarkdownPreviewLines = 60;
    private const int MarkdownPreviewLinesMore = 150;
    private const int OverloadBodyLineBudget = 80;
    private const int MaxOverloadListed = 8;

    public Task<int> RunAsync(CommandContext ctx)
    {
        var targets = ctx.Args.Where(a => !a.StartsWith('-')).ToArray();
        var flags = ctx.Args.Where(a => a.StartsWith('-')).ToArray();

        if (targets.Length == 0)
        {
            ctx.Err.WriteLine("tk view: file argument required");
            return Task.FromResult(1);
        }

        var exitCode = 0;
        for (var i = 0; i < targets.Length; i++)
        {
            if (i > 0)
                ctx.Out.WriteLine();
            var (output, targetExitCode) = Render(targets[i], flags, ctx.Raw, ctx.DetailLevel == DetailLevel.More);
            ctx.Out.Write(output);
            if (targetExitCode != 0)
                exitCode = targetExitCode;
        }

        return Task.FromResult(exitCode);
    }

    internal static (string Output, int ExitCode) Render(string target, string[] flags, bool ctxRaw, bool ctxMore)
    {
        var raw = ctxRaw || flags.Contains("--raw");
        var more = ctxMore || flags.Contains("--more");
        var symbolsOnly = flags.Contains("--symbols");
        var (pathTarget, symbolName) = SplitSymbolSuffix(target);
        var (path, startLine, endLine) = ParseTarget(pathTarget);

        if (!File.Exists(path))
            return ($"tk view: {path}: file not found\n", 1);

        if (LooksBinary(path))
            return ($"view {Path.GetFileName(path)} binary=1\n", 0);

        var lines = File.ReadAllLines(path);

        if (symbolName is not null)
        {
            var symbols = ExtractSymbols(lines).ToList();
            var matches = FindSymbolMatches(symbols, lines, symbolName);
            if (matches.Count == 0)
                return ($"tk view: {Path.GetFileName(path)}: symbol '{symbolName}' not found\n", 1);
            if (matches.Count == 1)
                return (RenderRange(path, lines, matches[0].Start, matches[0].End), 0);
            return (RenderSymbolMatches(path, lines, symbolName, matches), 0);
        }

        if (startLine.HasValue || endLine.HasValue)
            return (RenderRange(path, lines, startLine ?? 1, endLine ?? startLine ?? lines.Length), 0);

        if (raw)
            return (RenderWholeFile(path, lines), 0);

        var allSymbols = ExtractSymbols(lines).ToList();
        if (symbolsOnly)
            return (RenderSummary(path, lines, allSymbols, more), 0);

        var isMarkdown = Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".markdown", StringComparison.OrdinalIgnoreCase);
        var totalChars = lines.Sum(l => l.Length);
        var (lineLimit, charLimit) = isMarkdown
            ? (MarkdownLineLimit, MarkdownCharLimit)
            : (SmallFileLineLimit, SmallFileCharLimit);
        if (lines.Length <= lineLimit && totalChars <= charLimit)
            return (RenderWholeFile(path, lines), 0);

        if (isMarkdown)
            return (RenderMarkdownSummary(path, lines, allSymbols, more), 0);

        return (RenderSummary(path, lines, allSymbols, more), 0);
    }

    private static (string PathTarget, string? Symbol) SplitSymbolSuffix(string target)
    {
        var idx = target.LastIndexOf("::", StringComparison.Ordinal);
        if (idx <= 0)
            return (target, null);

        var symbol = target[(idx + 2)..];
        if (string.IsNullOrWhiteSpace(symbol))
            return (target, null);

        return (target[..idx], symbol);
    }

    /// <summary>
    /// Finds every symbol whose name matches (exact match preferred over case-insensitive
    /// over substring), so overloaded/repeated symbol names surface all declarations
    /// instead of silently returning just the first one.
    /// </summary>
    private static List<HotRange> FindSymbolMatches(List<Symbol> symbols, string[] lines, string name)
    {
        var ranges = BuildHotRanges(symbols, lines).ToList();

        var exact = ranges.Where(r => r.Name.Equals(name, StringComparison.Ordinal)).ToList();
        if (exact.Count > 0)
            return exact;

        var ci = ranges.Where(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (ci.Count > 0)
            return ci;

        return ranges.Where(r => r.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Renders N&gt;1 declarations matching a symbol name. Shows every body when the
    /// combined size is small; otherwise shows the first body plus an explicit manifest
    /// of the remaining matches with their line ranges. Always discloses "N matches".
    /// </summary>
    private static string RenderSymbolMatches(string path, string[] lines, string symbolName, List<HotRange> matches)
    {
        var sb = new StringBuilder();
        var totalBodyLines = matches.Sum(m => m.End - m.Start + 1);

        if (totalBodyLines <= OverloadBodyLineBudget)
        {
            sb.AppendLine($"view {Path.GetFileName(path)}::{symbolName} {matches.Count} matches (showing all)");
            for (var i = 0; i < matches.Count; i++)
            {
                if (i > 0)
                    sb.AppendLine();
                sb.Append(RenderRange(path, lines, matches[i].Start, matches[i].End));
            }

            return sb.ToString();
        }

        var first = matches[0];
        var others = matches.Skip(1).Take(MaxOverloadListed).Select(m => $"{m.Name}({m.Start}-{m.End})").ToList();
        var manifest = $"{first.Name}({first.Start}-{first.End}); also: {string.Join(", ", others)}";
        if (matches.Count - 1 > MaxOverloadListed)
            manifest += ", …";
        manifest += $" ({matches.Count} total)";

        sb.AppendLine($"view {Path.GetFileName(path)}::{symbolName} {matches.Count} matches (showing 1 of {matches.Count})");
        sb.AppendLine(manifest);
        sb.Append(RenderRange(path, lines, first.Start, first.End));
        return sb.ToString();
    }

    private static string RenderWholeFile(string path, string[] lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"view {Path.GetFileName(path)} lines={lines.Length}");
        AppendLines(sb, lines, 1, lines.Length);
        return sb.ToString();
    }

    private static string RenderRange(string path, string[] lines, int startLine, int endLine)
    {
        var clampedStart = Math.Max(1, startLine);
        var clampedEnd = Math.Min(lines.Length, Math.Max(clampedStart, endLine));

        var sb = new StringBuilder();
        sb.AppendLine($"view {Path.GetFileName(path)} {clampedStart}-{clampedEnd}/{lines.Length}");
        AppendLines(sb, lines, clampedStart, clampedEnd);
        return sb.ToString();
    }

    private static string RenderSummary(string path, string[] lines, List<Symbol> symbols, bool more)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"view {Path.GetFileName(path)} lines={lines.Length}");

        if (symbols.Count > 0)
        {
            var symbolCap = more ? MaxSymbolsMore : MaxSymbols;
            var shownSymbols = symbols.Take(symbolCap).ToList();
            sb.AppendLine($"symbols: {string.Join(", ", shownSymbols.Select(s => $"{s.Name}({s.Line})"))}");
            AppendHiddenFooter(sb, symbols.Count, shownSymbols.Count, "symbols", more);

            sb.AppendLine("hot:");
            var hotCap = more ? MaxHotRangesMore : MaxHotRanges;
            var allRanges = BuildHotRanges(symbols, lines).ToList();
            var shownRanges = allRanges.Take(hotCap).ToList();
            foreach (var range in shownRanges)
                sb.AppendLine($"  {range.Start}-{range.End} {range.Name}");
            AppendHiddenFooter(sb, allRanges.Count, shownRanges.Count, "hot", more);
        }

        return sb.ToString();
    }

    private static string RenderMarkdownSummary(string path, string[] lines, List<Symbol> symbols, bool more)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"view {Path.GetFileName(path)} lines={lines.Length}");

        if (symbols.Count > 0)
        {
            var symbolCap = more ? MaxSymbolsMore : MaxSymbols;
            var shownSymbols = symbols.Take(symbolCap).ToList();
            sb.AppendLine($"headings: {string.Join(", ", shownSymbols.Select(s => $"{s.Name}({s.Line})"))}");
            AppendHiddenFooter(sb, symbols.Count, shownSymbols.Count, "headings", more);
        }

        var previewCap = more ? MarkdownPreviewLinesMore : MarkdownPreviewLines;
        var shownLines = Math.Min(previewCap, lines.Length);
        AppendLines(sb, lines, 1, shownLines);

        var footer = HiddenLinesFooter.Format(lines.Length, shownLines, more ? DetailLevel.More : DetailLevel.Default);
        if (footer is not null)
            sb.AppendLine(footer);

        return sb.ToString();
    }

    /// <summary>Honest "hid=X/Y" disclosure when a capped list was truncated (see HiddenLinesFooter).</summary>
    private static void AppendHiddenFooter(StringBuilder sb, int total, int shown, string label, bool more)
    {
        if (shown >= total)
            return;

        var hidden = total - shown;
        var suffix = more ? "(--raw)" : "(--more, --raw)";
        sb.AppendLine($"  hid={hidden}/{total} {label} {suffix}");
    }

    private static void AppendLines(StringBuilder sb, string[] lines, int start, int end, string indent = "")
    {
        for (var i = start; i <= end; i++)
        {
            var content = Truncate(lines[i - 1], 180);
            sb.AppendLine($"{indent}{i,4}| {content}");
        }
    }

    private static (string Path, int? StartLine, int? EndLine) ParseTarget(string target)
    {
        if (File.Exists(target))
            return (target, null, null);

        var match = RangeSuffixRe().Match(target);
        if (!match.Success)
            return (target, null, null);

        var path = match.Groups["path"].Value;
        if (!int.TryParse(match.Groups["start"].Value, out var start))
            return (target, null, null);

        var end = int.TryParse(match.Groups["end"].Value, out var parsedEnd) ? parsedEnd : start;
        return (path, start, end);
    }

    private static bool LooksBinary(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> buffer = stackalloc byte[512];
        var read = stream.Read(buffer);
        return buffer[..read].IndexOf((byte)0) >= 0;
    }

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "..." : s;

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

            yield return new HotRange(current.Name, current.Line, end);
        }
    }

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

    [GeneratedRegex(@"^(?<path>.+):(?<start>\d+)(?:-(?<end>\d+))?$")]
    private static partial Regex RangeSuffixRe();

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

    private enum SymbolKind
    {
        Other,
        Type,
        Method,
        Property,
        JsArrow
    }

    private sealed record Symbol(string Name, int Line, SymbolKind Kind);
    private sealed record HotRange(string Name, int Start, int End);
}
