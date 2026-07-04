using System.Text;
using System.Text.RegularExpressions;
using Tk.Common;

namespace Tk.Filters;

/// <summary>
/// Summary-first grep/rg output for agents:
/// total matches, top files, and a few representative samples.
/// </summary>
public sealed partial class GrepFilter : IOutputFilter
{
    private readonly int _maxTopFiles;
    private readonly int _maxSamples;
    private readonly string _command;
    private readonly DetailLevel _detailLevel;
    private readonly string? _pattern;

    public GrepFilter(string command, DetailLevel detailLevel, string? pattern = null)
    {
        _command = command;
        _detailLevel = detailLevel;
        _pattern = string.IsNullOrWhiteSpace(pattern) ? null : pattern;
        _maxTopFiles = detailLevel == DetailLevel.More ? 6 : 3;
        _maxSamples = detailLevel == DetailLevel.More ? 6 : 3;
    }

    public string Apply(string raw, int exitCode) => Apply(raw, exitCode, new UnitLedger());

    public string Apply(string raw, int exitCode, UnitLedger ledger)
    {
        var totalLines = HiddenLinesFooter.CountLines(raw);

        if (string.IsNullOrWhiteSpace(raw))
        {
            if (exitCode <= 1)
            {
                ledger.Hide(totalLines);
                return $"{_command} m=0 f=0\n";
            }

            ledger.Keep(totalLines);
            return raw;
        }

        var rawLines = raw.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var blankCount = 0;
        var lines = new List<string>();
        foreach (var l in rawLines)
        {
            if (string.IsNullOrWhiteSpace(l))
                blankCount++;
            else
                lines.Add(l);
        }
        if (rawLines.Length > 0 && rawLines[^1] == string.Empty)
            blankCount--;

        if (lines.Count == 0)
        {
            if (exitCode <= 1)
            {
                ledger.Hide(blankCount);
                return $"{_command} m=0 f=0\n";
            }

            ledger.Keep(blankCount);
            return raw;
        }

        var entries = new List<GrepEntry>();
        var binaryCount = 0;
        var unparsedCount = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith("Binary file ") && line.EndsWith(" matches"))
            {
                binaryCount++;
                continue;
            }

            // file:line:content (grep -n)
            var m = GrepLineNumRe().Match(line);
            if (m.Success)
            {
                entries.Add(new GrepEntry(
                    m.Groups["file"].Value,
                    int.TryParse(m.Groups["line"].Value, out var n) ? n : 0,
                    m.Groups["content"].Value));
                continue;
            }

            // file:content (grep without -n)
            if (TryParseFileContent(line, out var file, out var content))
            {
                entries.Add(new GrepEntry(file, 0, content));
                continue;
            }

            // bare path (grep -l)
            if (line.Contains('/') || line.Contains('\\'))
            {
                entries.Add(new GrepEntry(line, 0, ""));
                continue;
            }

            // Not any recognized grep/rg output shape — alien input.
            unparsedCount++;
        }

        if (entries.Count == 0 && binaryCount == 0)
        {
            ledger.Hide(blankCount);
            ledger.Unparsed(unparsedCount);
            return raw;
        }

        var prefix = PathUtils.FindCommonPrefix(entries.Select(e => e.File).Distinct());
        var files = entries
            .GroupBy(e => PathUtils.StripPrefix(e.File, prefix))
            .Select(g => new FileGroup(g.Key, g.Count(), g.ToList()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Path, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.Append($"{_command} m={entries.Count} f={files.Count}");
        if (binaryCount > 0)
            sb.Append($" bin={binaryCount}");
        sb.AppendLine();

        if (files.Count > 0)
        {
            var top = string.Join(",",
                files.Take(_maxTopFiles).Select(f => $"{f.Path}({f.Count})"));
            sb.AppendLine($"top={top}");
        }

        var samples = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Content))
            .GroupBy(e => e.Content.Trim())
            .OrderByDescending(g => g.Count())
            .ToList();

        if (samples.Count > 0)
        {
            sb.AppendLine("samples:");
            foreach (var sample in samples.Take(_maxSamples))
            {
                var first = sample.First();
                var path = PathUtils.StripPrefix(first.File, prefix);
                var location = first.Line > 0 ? $"{path}:{first.Line}" : path;
                sb.AppendLine($"  {location} {TruncateAroundPattern(sample.Key, 120, _pattern)}");
            }
        }
        else if (files.Count > 0)
        {
            sb.AppendLine("paths:");
            foreach (var file in files.Take(_maxSamples))
                sb.AppendLine($"  {file.Path}");
        }

        if (_detailLevel == DetailLevel.More && files.Count > 0)
        {
            var remaining = files.Skip(_maxTopFiles).Take(_maxSamples);
            if (remaining.Any())
                sb.AppendLine($"more={string.Join(",", remaining.Select(f => $"{f.Path}({f.Count})"))}");
        }

        var extraFiles = files.Count - _maxTopFiles;
        if (extraFiles > 0)
            sb.AppendLine(Ansi.Dim($"+{extraFiles} more files"));

        // Ledger accounting — see docs/output-contract.md. Only the literal sample/path lines
        // actually rendered are Kept; every other match is represented via the m=/f=/top=
        // aggregate counts (Summarized), same as the binary-file matches (via bin=).
        ledger.Hide(blankCount);
        ledger.Unparsed(unparsedCount);

        if (samples.Count > 0)
        {
            var shownSamples = samples.Take(_maxSamples).ToList();
            foreach (var sample in shownSamples)
            {
                var count = sample.Count();
                if (count == 1) ledger.Keep(1);
                else ledger.Summarize(count);
            }
            ledger.Summarize(samples.Skip(_maxSamples).Sum(s => s.Count()));
        }
        else if (files.Count > 0)
        {
            var shownFiles = files.Take(_maxSamples).ToList();
            foreach (var file in shownFiles)
            {
                if (file.Count == 1) ledger.Keep(1);
                else ledger.Summarize(file.Count);
            }
            ledger.Summarize(files.Skip(_maxSamples).Sum(f => f.Count));
        }
        else
        {
            ledger.Summarize(entries.Count);
        }

        ledger.Summarize(binaryCount);

        return sb.ToString();
    }

    private static string TruncateAroundPattern(string s, int max, string? pattern)
    {
        if (s.Length <= max)
            return s;

        var patternIndex = pattern is null
            ? -1
            : s.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (patternIndex < 0)
            return s[..max] + "...";

        var start = Math.Max(0, patternIndex - (max / 3));
        if (start + max > s.Length)
            start = Math.Max(0, s.Length - max);

        var end = Math.Min(s.Length, start + max);
        var prefix = start > 0 ? "..." : "";
        var suffix = end < s.Length ? "..." : "";
        return prefix + s[start..end] + suffix;
    }

    private static bool TryParseFileContent(string line, out string file, out string content)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != ':')
                continue;

            if (i == 1 && char.IsLetter(line[0]))
                continue;

            file = line[..i];
            content = line[(i + 1)..];
            return !string.IsNullOrWhiteSpace(file);
        }

        file = "";
        content = "";
        return false;
    }

    [GeneratedRegex(@"^(?<file>.+?):(?<line>\d+):(?<content>.*)$")]
    private static partial Regex GrepLineNumRe();

    private record GrepEntry(string File, int Line, string Content);
    private record FileGroup(string Path, int Count, List<GrepEntry> Entries);
}
