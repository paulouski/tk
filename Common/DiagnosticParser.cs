using System.Text.RegularExpressions;

namespace Tk.Common;

public static partial class DiagnosticParser
{
    public static Diagnostic? Parse(string line)
    {
        var m = DiagnosticRe().Match(line);
        if (m.Success)
        {
            return new Diagnostic(
                File: m.Groups["file"].Value.Trim(),
                Line: int.TryParse(m.Groups["line"].Value, out var l) ? l : 0,
                Column: int.TryParse(m.Groups["col"].Value, out var c) ? c : 0,
                Kind: m.Groups["kind"].Value.ToLowerInvariant(),
                Code: m.Groups["code"].Value,
                Message: m.Groups["msg"].Value.Trim(),
                Project: ExtractProject(m.Groups["proj"].Value));
        }

        var t = ToolDiagnosticRe().Match(line);
        if (t.Success)
        {
            return new Diagnostic(
                File: t.Groups["source"].Value.Trim(),
                Line: 0,
                Column: 0,
                Kind: t.Groups["kind"].Value.ToLowerInvariant(),
                Code: t.Groups["code"].Value,
                Message: t.Groups["msg"].Value.Trim(),
                Project: "");
        }

        return null;
    }

    private static string ExtractProject(string raw)
    {
        var trimmed = raw.Trim().Trim('[', ']');
        return Path.GetFileNameWithoutExtension(trimmed);
    }

    [GeneratedRegex(@"^(?<file>[^\r\n(]+)\((?<line>\d+),(?<col>\d+)\):\s*(?<kind>error|warning)\s+(?<code>[A-Za-z]*\d*)\s*:\s*(?<msg>[^\[]+?)(?<proj>\[[^\]]*\])?\s*$")]
    private static partial Regex DiagnosticRe();

    [GeneratedRegex(@"^(?<source>.+?)\s*:\s*(?<kind>error|warning)\s+(?<code>[A-Za-z]*\d+)\s*:\s*(?<msg>.+)$")]
    private static partial Regex ToolDiagnosticRe();
}
