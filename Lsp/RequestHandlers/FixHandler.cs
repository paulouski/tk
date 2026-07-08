using System.Text.Json;
using Tk.Lsp.Protocol;

namespace Tk.Lsp.RequestHandlers;

/// <summary>
/// Handles <c>fix</c> — the restricted "add missing using / remove unnecessary using" flow
/// for a single file: pulls diagnostics, keeps only the fixable CS0246/CS8019/IDE0005 codes,
/// requests a textDocument/codeAction per diagnostic, and keeps only actions whose title
/// matches the safe subset — never anything else, and never partially applies an action that
/// would require a workspace/executeCommand round-trip this daemon does not implement.
/// </summary>
internal sealed class FixHandler : IRequestHandler
{
    /// <summary>
    /// The two diagnostic families <c>tk fix</c> is allowed to act on: CS0246 ("type or
    /// namespace could not be found", a missing-using candidate) and CS8019/IDE0005
    /// ("unnecessary using directive", a remove-using candidate). Nothing else is ever sent
    /// to codeAction — the safe subset is enforced here, before any protocol round-trip.
    /// </summary>
    private static readonly HashSet<string> FixableDiagnosticCodes = new(StringComparer.Ordinal)
    {
        "CS0246", "CS8019", "IDE0005",
    };

    private enum UsingFixKind { Add, Remove }

    // Roslyn's own quickfix title for an add-using action is literally the using directive
    // text it would insert (e.g. "using System.Text.RegularExpressions;").
    private static readonly System.Text.RegularExpressions.Regex UsingDirectiveTitleRegex =
        new(@"^using\s+[A-Za-z_][\w.]*;$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<DaemonResponse> HandleAsync(LspDaemonContext ctx, DaemonRequest request, CancellationToken ct)
    {
        if (request.FilePath is null)
            return new DaemonResponse(false, $"Unknown method: {request.Method}", null);

        await ctx.WaitForReadyAsync(ct).ConfigureAwait(false);
        ctx.Log($"fix query: {request.FilePath}");
        var (fixEdits, fixSummary) = await ComputeUsingFixesAsync(ctx.Loop, ctx.EnsureFileOpenAsync, request.FilePath, ct).ConfigureAwait(false);
        ctx.Log($"fix query done, supported={fixSummary.Supported} added={fixSummary.UsingsAdded} removed={fixSummary.UsingsRemoved}");
        return new DaemonResponse(true, null, null) with { Edits = fixEdits, Fix = fixSummary };
    }

    /// <summary>
    /// Computes the restricted "add missing using / remove unnecessary using" fix for a single
    /// file: pulls diagnostics, keeps only <see cref="FixableDiagnosticCodes"/>, requests a
    /// textDocument/codeAction per diagnostic, and keeps only actions whose title matches the
    /// safe subset — never anything else, and never partially applies an action that would
    /// require a workspace/executeCommand round-trip this daemon does not implement (see
    /// <see cref="RequestUsingCodeActionAsync"/>).
    /// </summary>
    private static async Task<(FileEdits[] Edits, FixSummary Summary)> ComputeUsingFixesAsync(
        MessageLoop loop, Func<string, string, CancellationToken, Task> ensureFileOpenAsync,
        string filePath, CancellationToken ct)
    {
        var fileUri = new Uri(filePath).ToString();
        await ensureFileOpenAsync(filePath, fileUri, ct).ConfigureAwait(false);

        var diagnostics = await DiagHandler.FindFileDiagnosticsAsync(loop, ensureFileOpenAsync, filePath, ct).ConfigureAwait(false);
        var relevant = diagnostics.Where(d => d.Code is not null && FixableDiagnosticCodes.Contains(d.Code)).ToList();

        if (relevant.Count == 0)
            return ([], new FixSummary(true, 0, 0, null));

        var collectedEdits = new List<RenameTextEdit>();
        var added = 0;
        var removed = 0;
        var sawUnresolvable = false;

        foreach (var diag in relevant)
        {
            var action = await RequestUsingCodeActionAsync(loop, fileUri, diag, ct).ConfigureAwait(false);
            if (action is null)
            {
                sawUnresolvable = true;
                continue;
            }

            var (kind, edits) = action.Value;
            collectedEdits.AddRange(edits);
            if (kind == UsingFixKind.Add) added++;
            else removed++;
        }

        if (collectedEdits.Count == 0)
        {
            var note = sawUnresolvable
                ? "server offered no matching add/remove-using quick fix for the detected diagnostics (or it would require an unsupported workspace/executeCommand round-trip)"
                : null;
            return ([], new FixSummary(!sawUnresolvable, 0, 0, note));
        }

        return ([new FileEdits(fileUri, [.. collectedEdits])], new FixSummary(true, added, removed, null));
    }

    /// <summary>
    /// Requests textDocument/codeAction for one diagnostic and returns the edits of the first
    /// action whose title is in the safe add/remove-using subset — resolving it via
    /// codeAction/resolve first if the server didn't include an "edit" inline. Returns null if
    /// no safe action was offered, or the only safe-titled action never yields a concrete edit
    /// (e.g. it only carries a "command" — that would need workspace/executeCommand, which this
    /// daemon does not implement; skipped rather than half-applied).
    /// </summary>
    private static async Task<(UsingFixKind Kind, List<RenameTextEdit> Edits)?> RequestUsingCodeActionAsync(
        MessageLoop loop, string fileUri, LspDiagnostic diag, CancellationToken ct)
    {
        var range = new
        {
            start = new { line = diag.Line, character = diag.Character },
            end = new { line = diag.EndLine, character = diag.EndChar }
        };
        var wireDiag = new { range, severity = LspResultParser.DiagnosticSeverityNumber(diag.Severity), code = diag.Code, message = diag.Message };

        var codeActionParams = new
        {
            textDocument = new { uri = fileUri },
            range,
            context = new { diagnostics = new[] { wireDiag }, only = new[] { "quickfix" } }
        };

        var result = await loop.SendRequestAsync("textDocument/codeAction", codeActionParams, ct).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in result.EnumerateArray())
        {
            if (!item.TryGetProperty("title", out var titleProp))
                continue;
            var title = titleProp.GetString() ?? "";

            UsingFixKind kind;
            if (UsingDirectiveTitleRegex.IsMatch(title))
                kind = UsingFixKind.Add;
            else if (title.Equals("Remove Unnecessary Usings", StringComparison.OrdinalIgnoreCase))
                kind = UsingFixKind.Remove;
            else
                continue; // outside the safe subset — never requested to resolve, never applied

            JsonElement? edit = item.TryGetProperty("edit", out var editProp) ? editProp : null;
            if (edit is null && item.TryGetProperty("data", out _))
            {
                var resolved = await loop.SendRequestAsync("codeAction/resolve", item, ct).ConfigureAwait(false);
                if (resolved.TryGetProperty("edit", out var resolvedEdit))
                    edit = resolvedEdit;
            }

            if (edit is null)
                continue; // no concrete edit available without workspace/executeCommand — skip

            var edits = LspResultParser.ParseFileEditsForUri(edit.Value, fileUri);
            if (edits.Count == 0)
                continue;

            return (kind, edits);
        }

        return null;
    }
}
