using System.Text;
using System.Text.RegularExpressions;

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

    public GrepFilter(string command, DetailLevel detailLevel)
    {
        _command = command;
        _detailLevel = detailLevel;
        _maxTopFiles = detailLevel == DetailLevel.More ? 6 : 3;
        _maxSamples = detailLevel == DetailLevel.More ? 6 : 3;
    }

    public string Apply(string raw, int exitCode)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return exitCode <= 1 ? $"{_command} m=0 f=0\n" : raw;

        var lines = raw.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        if (lines.Length == 0)
            return exitCode <= 1 ? $"{_command} m=0 f=0\n" : raw;

        var entries = new List<GrepEntry>();
        var binaryCount = 0;

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

            // bare path (grep -l) or unparseable
            if (line.Contains('/') || line.Contains('\\'))
                entries.Add(new GrepEntry(line, 0, ""));
        }

        if (entries.Count == 0 && binaryCount == 0)
            return raw;

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
                sb.AppendLine($"  {location} {Truncate(sample.Key, 120)}");
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

        return sb.ToString();
    }

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "..." : s;

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
