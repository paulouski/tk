using System.Text;
using System.Text.RegularExpressions;

namespace Tk.Commands;

public sealed partial class ViewCommand : ICommand
{
    public string Name => "view";

    private const int SmallFileLineLimit = 40;
    private const int SmallFileCharLimit = 3000;
    private const int MaxSymbols = 8;
    private const int MaxHotRanges = 5;

    public Task<int> RunAsync(CommandContext ctx)
    {
        var targets = ctx.Args.Where(a => !a.StartsWith('-')).ToArray();
        var flags = ctx.Args.Where(a => a.StartsWith('-')).ToArray();

        if (targets.Length == 0)
        {
            ctx.Err.WriteLine("tk view: file argument required");
            return Task.FromResult(1);
        }

        for (var i = 0; i < targets.Length; i++)
        {
            if (i > 0)
                ctx.Out.WriteLine();
            ctx.Out.Write(Render(targets[i], flags, ctx.Raw, ctx.DetailLevel == DetailLevel.More));
        }

        return Task.FromResult(0);
    }

    internal static string Render(string target, string[] flags, bool ctxRaw, bool ctxMore)
    {
        var raw = ctxRaw || flags.Contains("--raw");
        var more = ctxMore || flags.Contains("--more");
        var symbolsOnly = flags.Contains("--symbols");
        var (pathTarget, symbolName) = SplitSymbolSuffix(target);
        var (path, startLine, endLine) = ParseTarget(pathTarget);

        if (!File.Exists(path))
            return $"tk view: {path}: file not found\n";

        if (LooksBinary(path))
            return $"view {Path.GetFileName(path)} binary=1\n";

        var lines = File.ReadAllLines(path);

        if (symbolName is not null)
        {
            var symbols = ExtractSymbols(lines).ToList();
            var range = FindSymbolRange(symbols, lines.Length, symbolName);
            if (range is null)
                return $"tk view: {Path.GetFileName(path)}: symbol '{symbolName}' not found\n";
            return RenderRange(path, lines, range.Value.Start, range.Value.End);
        }

        if (startLine.HasValue || endLine.HasValue)
            return RenderRange(path, lines, startLine ?? 1, endLine ?? startLine ?? lines.Length);

        if (raw)
            return RenderWholeFile(path, lines);

        var allSymbols = ExtractSymbols(lines).ToList();
        if (symbolsOnly)
            return RenderSummary(path, lines, allSymbols, more);

        var totalChars = lines.Sum(l => l.Length);
        if (lines.Length <= SmallFileLineLimit && totalChars <= SmallFileCharLimit)
            return RenderWholeFile(path, lines);

        return RenderSummary(path, lines, allSymbols, more);
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

    private static (int Start, int End)? FindSymbolRange(List<Symbol> symbols, int totalLines, string name)
    {
        var ranges = BuildHotRanges(symbols, totalLines).ToList();
        var exact = ranges.FirstOrDefault(r => r.Name.Equals(name, StringComparison.Ordinal));
        if (exact is not null)
            return (exact.Start, exact.End);

        var ci = ranges.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (ci is not null)
            return (ci.Start, ci.End);

        var contains = ranges.FirstOrDefault(r => r.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        return contains is null ? null : (contains.Start, contains.End);
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
            sb.AppendLine($"symbols: {string.Join(", ", symbols.Take(MaxSymbols).Select(s => $"{s.Name}({s.Line})"))}");
            sb.AppendLine("hot:");

            foreach (var range in BuildHotRanges(symbols, lines.Length).Take(more ? MaxHotRanges + 3 : MaxHotRanges))
                sb.AppendLine($"  {range.Start}-{range.End} {range.Name}");
        }

        return sb.ToString();
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
            return new Symbol(markdown.Groups["name"].Value.Trim(), lineNumber);

        var csType = TypeDeclRe().Match(line);
        if (csType.Success)
            return new Symbol(csType.Groups["name"].Value, lineNumber);

        var csMethod = MethodDeclRe().Match(line);
        if (csMethod.Success)
            return new Symbol(csMethod.Groups["name"].Value, lineNumber);

        var pyDef = PythonDeclRe().Match(line);
        if (pyDef.Success)
            return new Symbol(pyDef.Groups["name"].Value, lineNumber);

        var jsFunc = JsFunctionRe().Match(line);
        if (jsFunc.Success)
            return new Symbol(jsFunc.Groups["name"].Value, lineNumber);

        var jsConst = JsConstFunctionRe().Match(line);
        if (jsConst.Success)
            return new Symbol(jsConst.Groups["name"].Value, lineNumber);

        return null;
    }

    private static IEnumerable<HotRange> BuildHotRanges(List<Symbol> symbols, int totalLines)
    {
        for (var i = 0; i < symbols.Count; i++)
        {
            var current = symbols[i];
            var nextLine = i + 1 < symbols.Count ? symbols[i + 1].Line : totalLines + 1;
            var end = Math.Max(current.Line, nextLine - 1);
            yield return new HotRange(current.Name, current.Line, Math.Min(end, totalLines));
        }
    }

    [GeneratedRegex(@"^(?<path>.+):(?<start>\d+)(?:-(?<end>\d+))?$")]
    private static partial Regex RangeSuffixRe();

    [GeneratedRegex(@"^#{1,6}\s+(?<name>.+)$")]
    private static partial Regex MarkdownHeadingRe();

    [GeneratedRegex(@"\b(class|interface|record|struct|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex TypeDeclRe();

    [GeneratedRegex(@"^(?:public|private|protected|internal|static|sealed|partial|async|virtual|override|abstract|extern|new|\s)+[\w<>\[\],?.]+\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex MethodDeclRe();

    [GeneratedRegex(@"^(?:async\s+)?(?:def|class)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex PythonDeclRe();

    [GeneratedRegex(@"^(?:export\s+)?(?:async\s+)?function\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex JsFunctionRe();

    [GeneratedRegex(@"^(?:export\s+)?(?:const|let|var)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?:async\s*)?\(")]
    private static partial Regex JsConstFunctionRe();

    private sealed record Symbol(string Name, int Line);
    private sealed record HotRange(string Name, int Start, int End);
}
