namespace Tk.Lsp;

/// <summary>
/// Formats `tk fix` outcomes into compact tk-style output. `tk fix` restricts
/// textDocument/codeAction to a safe subset (add missing using / remove unnecessary using);
/// see <see cref="LspDaemon"/>'s ComputeUsingFixesAsync for the actual protocol flow.
/// </summary>
public static class FixFormatter
{
    /// <summary>
    /// The server offered nothing in the safe subset, or every candidate action needed a
    /// workspace/executeCommand round-trip this daemon does not implement.
    /// </summary>
    public static string FormatUnsupported(string file, FixSummary? summary)
    {
        var note = summary?.Note is { Length: > 0 } n ? $" — {n}" : "";
        return $"fix {file}: unsupported by server{note}";
    }

    /// <summary>No fixable diagnostics were found in the file — nothing to apply.</summary>
    public static string FormatNothingToFix(string file) =>
        $"ok fix {file}: nothing to fix (added=0 removed=0)";

    /// <summary>At least one using was added or removed and the edit was applied.</summary>
    public static string FormatApplied(string file, FixSummary summary) =>
        $"ok fix {file}: added={summary.UsingsAdded} removed={summary.UsingsRemoved}";
}
