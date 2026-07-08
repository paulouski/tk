using System.Text;
using System.Text.RegularExpressions;
using Tk.Commands.View;
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

    // Provider chain: probe LSP first for .cs files (semantic ranges + signatures), then
    // YAML for .yml/.yaml (workflow structure for GHA files, top-level keys otherwise),
    // then regex (approximate, file-type-agnostic fallback). All three are stateless; the
    // instances are reused across calls. Marked non-readonly so tests can swap them
    // (see SetProvidersForTest); in production the defaults are never reassigned.
    private static IFileOutlineProvider LspProvider = new CSharpLspOutlineProvider();
    private static IFileOutlineProvider YamlProvider = new YamlOutlineProvider();
    private static IFileOutlineProvider RegexProvider = new RegexOutlineProvider();

    /// <summary>
    /// Test seam: lets a unit test swap in a fake <see cref="IFileOutlineProvider"/> (e.g.
    /// one that talks to a fake daemon listener) without touching the production provider
    /// chain. Reset to the defaults via <see cref="ResetProvidersForTest"/>. The YAML
    /// provider is left at its production default — the chain checks <c>CanHandle</c> per
    /// file so .cs/.md test fixtures never invoke it.
    /// </summary>
    internal static void SetProvidersForTest(IFileOutlineProvider lsp, IFileOutlineProvider regex)
    {
        LspProvider = lsp;
        RegexProvider = regex;
    }

    internal static void ResetProvidersForTest()
    {
        LspProvider = new CSharpLspOutlineProvider();
        RegexProvider = new RegexOutlineProvider();
    }

    public async Task<int> RunAsync(CommandContext ctx)
    {
        var targets = ctx.Args.Where(a => !a.StartsWith('-')).ToArray();
        var flags = ctx.Args.Where(a => a.StartsWith('-')).ToArray();

        if (targets.Length == 0)
        {
            ctx.Err.WriteLine("tk view: file argument required");
            return 1;
        }

        var exitCode = 0;
        for (var i = 0; i < targets.Length; i++)
        {
            if (i > 0)
                ctx.Out.WriteLine();
            var (output, targetExitCode) = await RenderAsync(targets[i], flags, ctx.Raw, ctx.DetailLevel == DetailLevel.More, CancellationToken.None).ConfigureAwait(false);
            ctx.Out.Write(output);
            if (targetExitCode != 0)
                exitCode = targetExitCode;
        }

        return exitCode;
    }

    internal static async Task<(string Output, int ExitCode)> RenderAsync(string target, string[] flags, bool ctxRaw, bool ctxMore, CancellationToken ct)
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
            // Symbol-suffix path: unchanged exact body/range renderer. Reads entries from
            // the regex provider so we don't re-implement the regex extractors here.
            var entries = await RegexProvider.GetOutlineAsync(path, ct).ConfigureAwait(false);
            var effectiveEntries = entries?.Entries ?? [];
            var matches = FindSymbolMatches(effectiveEntries, symbolName);
            if (matches.Count == 0)
                return ($"tk view: {Path.GetFileName(path)}: symbol '{symbolName}' not found\n", 1);
            if (matches.Count == 1)
                return (RenderRange(path, lines, matches[0].StartLine, matches[0].EndLine), 0);
            return (RenderSymbolMatches(path, lines, symbolName, matches), 0);
        }

        if (startLine.HasValue || endLine.HasValue)
            return (RenderRange(path, lines, startLine ?? 1, endLine ?? startLine ?? lines.Length), 0);

        if (raw)
            return (RenderWholeFile(path, lines), 0);

        if (symbolsOnly)
        {
            // --symbols keeps the legacy two-section ("symbols:" + "hot:") format for
            // back-compat; the new default path below is the preferred outline format.
            var entries = await RegexProvider.GetOutlineAsync(path, ct).ConfigureAwait(false);
            return (RenderSummary(path, lines, entries?.Entries ?? [], more), 0);
        }

        var isMarkdown = Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".markdown", StringComparison.OrdinalIgnoreCase);
        var totalChars = lines.Sum(l => l.Length);
        var (lineLimit, charLimit) = isMarkdown
            ? (MarkdownLineLimit, MarkdownCharLimit)
            : (SmallFileLineLimit, SmallFileCharLimit);
        if (lines.Length <= lineLimit && totalChars <= charLimit)
            return (RenderWholeFile(path, lines), 0);

        if (isMarkdown)
        {
            // Markdown keeps the old headings+preview format; the new outline is for code.
            var entries = await RegexProvider.GetOutlineAsync(path, ct).ConfigureAwait(false);
            return (RenderMarkdownSummary(path, lines, entries?.Entries ?? [], more), 0);
        }

        // Default path on a large non-markdown file: probe each provider in turn until
        // one returns a non-null outline. The chain is LSP (semantic for .cs) → YAML
        // (workflow structure for .yml/.yaml) → regex (approximate, file-type-agnostic
        // fallback). All three return null on any failure path so this code never throws.
        OutlineResult? outline = null;
        if (LspProvider.CanHandle(path))
        {
            try
            {
                outline = await LspProvider.GetOutlineAsync(path, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                outline = null;
            }
        }

        if (outline is null && YamlProvider.CanHandle(path))
        {
            try
            {
                outline = await YamlProvider.GetOutlineAsync(path, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                outline = null;
            }
        }

        outline ??= await RegexProvider.GetOutlineAsync(path, ct).ConfigureAwait(false);

        if (outline is null)
            return (RenderWholeFile(path, lines), 0);

        return outline.Source switch
        {
            "lsp" => (RenderLspOutline(path, lines.Length, outline.Entries, more), 0),
            "yaml" => (RenderYamlOutline(path, lines.Length, outline.Entries, more), 0),
            _ => (RenderRegexOutline(path, lines.Length, outline.Entries, more), 0),
        };
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
    /// Finds every entry whose name matches (exact match preferred over case-insensitive
    /// over substring), so overloaded/repeated symbol names surface all declarations
    /// instead of silently returning just the first one.
    /// </summary>
    private static List<OutlineEntry> FindSymbolMatches(List<OutlineEntry> entries, string name)
    {
        var exact = entries.Where(e => e.Name.Equals(name, StringComparison.Ordinal)).ToList();
        if (exact.Count > 0)
            return exact;

        var ci = entries.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (ci.Count > 0)
            return ci;

        return entries.Where(e => e.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Renders N&gt;1 declarations matching a symbol name. Shows every body when the
    /// combined size is small; otherwise shows the first body plus an explicit manifest
    /// of the remaining matches with their line ranges. Always discloses "N matches".
    /// </summary>
    private static string RenderSymbolMatches(string path, string[] lines, string symbolName, List<OutlineEntry> matches)
    {
        var sb = new StringBuilder();
        var totalBodyLines = matches.Sum(m => m.EndLine - m.StartLine + 1);

        if (totalBodyLines <= OverloadBodyLineBudget)
        {
            sb.AppendLine($"view {Path.GetFileName(path)}::{symbolName} {matches.Count} matches (showing all)");
            for (var i = 0; i < matches.Count; i++)
            {
                if (i > 0)
                    sb.AppendLine();
                sb.Append(RenderRange(path, lines, matches[i].StartLine, matches[i].EndLine));
            }

            return sb.ToString();
        }

        var first = matches[0];
        var others = matches.Skip(1).Take(MaxOverloadListed).Select(m => $"{m.Name}({m.StartLine}-{m.EndLine})").ToList();
        var manifest = $"{first.Name}({first.StartLine}-{first.EndLine}); also: {string.Join(", ", others)}";
        if (matches.Count - 1 > MaxOverloadListed)
            manifest += ", …";
        manifest += $" ({matches.Count} total)";

        sb.AppendLine($"view {Path.GetFileName(path)}::{symbolName} {matches.Count} matches (showing 1 of {matches.Count})");
        sb.AppendLine(manifest);
        sb.Append(RenderRange(path, lines, first.StartLine, first.EndLine));
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

    /// <summary>
    /// Legacy <c>--symbols</c> renderer: two-section format (<c>symbols:</c> flat list of
    /// name(line), then <c>hot:</c> range+name list). Preserved unchanged for back-compat
    /// with callers that key on those exact substrings. The new default outline is
    /// <see cref="RenderLspOutline"/> / <see cref="RenderRegexOutline"/>; the spec keeps
    /// this as the explicit <c>--symbols</c> path.
    /// </summary>
    private static string RenderSummary(string path, string[] lines, List<OutlineEntry> entries, bool more)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"view {Path.GetFileName(path)} lines={lines.Length}");

        if (entries.Count > 0)
        {
            var symbolCap = more ? MaxSymbolsMore : MaxSymbols;
            var shownSymbols = entries.Take(symbolCap).ToList();
            sb.AppendLine($"symbols: {string.Join(", ", shownSymbols.Select(e => $"{e.Name}({e.StartLine})"))}");
            AppendHiddenFooter(sb, entries.Count, shownSymbols.Count, "symbols", more);

            sb.AppendLine("hot:");
            var hotCap = more ? MaxHotRangesMore : MaxHotRanges;
            var shownRanges = entries.Take(hotCap).ToList();
            foreach (var range in shownRanges)
                sb.AppendLine($"  {range.StartLine}-{range.EndLine} {range.Name}");
            AppendHiddenFooter(sb, entries.Count, shownRanges.Count, "hot", more);
        }

        return sb.ToString();
    }

    private static string RenderMarkdownSummary(string path, string[] lines, List<OutlineEntry> entries, bool more)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"view {Path.GetFileName(path)} lines={lines.Length}");

        if (entries.Count > 0)
        {
            var symbolCap = more ? MaxSymbolsMore : MaxSymbols;
            var shownSymbols = entries.Take(symbolCap).ToList();
            sb.AppendLine($"headings: {string.Join(", ", shownSymbols.Select(e => $"{e.Name}({e.StartLine})"))}");
            AppendHiddenFooter(sb, entries.Count, shownSymbols.Count, "headings", more);
        }

        var previewCap = more ? MarkdownPreviewLinesMore : MarkdownPreviewLines;
        var shownLines = Math.Min(previewCap, lines.Length);
        AppendLines(sb, lines, 1, shownLines);

        // view isn't ledger-ized this phase (see docs/output-contract.md) — unparsedCount is
        // always 0, so this is byte-identical to the pre-existing HiddenLinesFooter.Format call.
        var footer = OutputFooter.Format(lines.Length, shownLines, unparsedCount: 0, more ? DetailLevel.More : DetailLevel.Default);
        if (footer is not null)
            sb.AppendLine(footer);

        return sb.ToString();
    }

    /// <summary>
    /// Renders the LSP-backed hierarchical outline. First line is the file card
    /// (<c>source=lsp symbols=N</c>), then one line per top-level entry (kind, name, optional
    /// signature detail, line range, body size), with children indented 2 spaces per nesting
    /// level. Trailing <c>hid=N/total symbols (--more)</c> footer when truncated.
    /// </summary>
    private static string RenderLspOutline(string path, int totalLines, List<OutlineEntry> entries, bool more)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"view {Path.GetFileName(path)} lines={totalLines} source=lsp symbols={entries.Count}");

        var cap = more ? MaxSymbolsMore : MaxSymbols;
        var shown = entries.Take(cap).ToList();
        foreach (var entry in shown)
            AppendLspEntry(sb, entry, indent: 0);

        AppendHiddenFooter(sb, entries.Count, shown.Count, "symbols", more);

        return sb.ToString();
    }

    private static void AppendLspEntry(StringBuilder sb, OutlineEntry entry, int indent)
    {
        var prefix = new string(' ', indent * 2);
        var nameWithDetail = entry.Detail is null ? entry.Name : $"{entry.Name}({entry.Detail})";
        sb.AppendLine($"{prefix}{entry.Kind} {nameWithDetail} {entry.StartLine}-{entry.EndLine} body={entry.BodySize}");

        if (entry.Children is { Count: > 0 })
            foreach (var child in entry.Children)
                AppendLspEntry(sb, child, indent + 1);
    }

    /// <summary>
    /// Renders the regex-backed flat outline. Same shape as the LSP variant minus nesting
    /// and the signature detail (regex can't recover method signatures), and the source
    /// tag is <c>regex approx</c> to signal the ranges are approximate.
    /// </summary>
    private static string RenderRegexOutline(string path, int totalLines, List<OutlineEntry> entries, bool more)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"view {Path.GetFileName(path)} lines={totalLines} source=regex approx symbols={entries.Count}");

        var cap = more ? MaxSymbolsMore : MaxSymbols;
        var shown = entries.Take(cap).ToList();
        foreach (var entry in shown)
            sb.AppendLine($"{entry.Kind} {entry.Name} {entry.StartLine}-{entry.EndLine} body={entry.BodySize}");

        AppendHiddenFooter(sb, entries.Count, shown.Count, "symbols", more);

        return sb.ToString();
    }

    /// <summary>
    /// Renders the YAML-backed outline. Workflow files (<c>name</c>/<c>on</c>/<c>jobs</c>
    /// present) get a workflow-shaped output: <c>workflow: &lt;name&gt;</c>, <c>on: &lt;events&gt;</c>,
    /// then a <c>jobs:</c> section with one indented line per job showing its range and
    /// step count. Generic YAML renders each top-level key with its line range. Source
    /// tag is <c>source=yaml</c> so callers can distinguish structure-only output from
    /// LSP/regex outlines.
    /// </summary>
    private static string RenderYamlOutline(string path, int totalLines, List<OutlineEntry> entries, bool more)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"view {Path.GetFileName(path)} lines={totalLines} source=yaml");

        var cap = more ? MaxSymbolsMore : MaxSymbols;
        var shown = entries.Take(cap).ToList();
        var isWorkflow = entries.Any(e => e.Kind is "meta" or "on" or "section" or "job");

        if (isWorkflow)
        {
            foreach (var entry in shown)
            {
                switch (entry.Kind)
                {
                    case "meta":
                        sb.AppendLine($"workflow: {entry.Name}");
                        break;
                    case "on":
                        sb.AppendLine($"on: {entry.Name}");
                        break;
                    case "section":
                        sb.AppendLine($"{entry.Name}:");
                        break;
                    case "job":
                        var steps = entry.Detail ?? "0";
                        sb.AppendLine($"  {entry.Name} {entry.StartLine}-{entry.EndLine} steps={steps}");
                        break;
                }
            }
        }
        else
        {
            foreach (var entry in shown)
                sb.AppendLine($"{entry.Name} {entry.StartLine}-{entry.EndLine}");
        }

        AppendHiddenFooter(sb, entries.Count, shown.Count, "entries", more);

        return sb.ToString();
    }

    /// <summary>Honest "hid=X/Y" disclosure when a capped list was truncated (see HiddenLinesFooter).</summary>
    private static void AppendHiddenFooter(StringBuilder sb, int total, int shown, string label, bool more)
    {
        if (shown >= total)
            return;

        var hidden = total - shown;
        var suffix = more ? "(--raw)" : "(--more)";
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

    [GeneratedRegex(@"^(?<path>.+):(?<start>\d+)(?:-(?<end>\d+))?$")]
    private static partial Regex RangeSuffixRe();
}
