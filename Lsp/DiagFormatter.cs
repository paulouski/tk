namespace Tk.Lsp;

/// <summary>
/// Formats LSP pull-diagnostics results into compact tk-style output for `tk diag`.
/// </summary>
public static class DiagFormatter
{
    private readonly record struct DiagLine(string File, int Line, string Severity, string? Code, string Message);

    /// <summary>
    /// Formats diagnostics gathered across one or more files.
    /// Header: <c>ok diag e=&lt;errors&gt; w=&lt;warnings&gt;</c> or
    /// <c>FAIL diag e=&lt;errors&gt; w=&lt;warnings&gt;</c> (w= omitted when
    /// <paramref name="errorsOnly"/>), followed by one line per diagnostic:
    /// <c>  file:line severity CODE: message</c>, errors first, each sorted by file then
    /// line. Diagnostics outside {error, warning} (info/hint) are dropped — this mirrors
    /// what `dotnet build` itself reports. Identical diagnostics (same file/line/severity/
    /// code/message) are deduped, following DotnetBuildFilter's dedup convention.
    /// When <paramref name="scopedFileCount"/> is less than <paramref name="totalFileCount"/>
    /// (the project/dir scope was capped), the header discloses it with a `--more` hint.
    /// Returns the rendered text and the number of distinct errors (for exit-code decisions).
    /// </summary>
    public static (string Output, int ErrorCount) Format(
        IReadOnlyList<FileDiagnostics> byFile,
        bool errorsOnly,
        int scopedFileCount = 0,
        int totalFileCount = 0)
    {
        var seen = new HashSet<(string File, int Line, string Severity, string Code, string Message)>();
        var errors = new List<DiagLine>();
        var warnings = new List<DiagLine>();

        foreach (var fd in byFile)
        {
            var file = RefsFormatter.UriToPath(fd.Uri);
            foreach (var d in fd.Diagnostics)
            {
                if (d.Severity != "error" && d.Severity != "warning")
                    continue;
                if (errorsOnly && d.Severity != "error")
                    continue;

                var key = (file, d.Line, d.Severity, d.Code ?? "", d.Message);
                if (!seen.Add(key))
                    continue;

                var line = new DiagLine(file, d.Line, d.Severity, d.Code, d.Message);
                (d.Severity == "error" ? errors : warnings).Add(line);
            }
        }

        errors = [.. errors.OrderBy(d => d.File, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Line)];
        warnings = [.. warnings.OrderBy(d => d.File, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Line)];

        var sb = new System.Text.StringBuilder();
        var status = errors.Count > 0 ? "FAIL" : "ok";
        sb.Append($"{status} diag e={errors.Count}");
        if (!errorsOnly)
            sb.Append($" w={warnings.Count}");
        if (scopedFileCount > 0 && scopedFileCount < totalFileCount)
            sb.Append($" f={scopedFileCount}/{totalFileCount} (--more)");

        foreach (var d in errors)
        {
            sb.AppendLine();
            sb.Append(FormatLine(d));
        }

        if (!errorsOnly)
        {
            foreach (var d in warnings)
            {
                sb.AppendLine();
                sb.Append(FormatLine(d));
            }
        }

        return (sb.ToString(), errors.Count);
    }

    private static string FormatLine(DiagLine d)
    {
        var code = string.IsNullOrEmpty(d.Code) ? "" : $" {d.Code}";
        return $"  {d.File}:{d.Line + 1} {d.Severity}{code}: {d.Message}";
    }
}
