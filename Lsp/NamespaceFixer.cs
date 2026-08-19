using System.Text.RegularExpressions;
using Tk.Commands;
using Tk.Modules;

namespace Tk.Lsp;

/// <summary>
/// Outcome of <see cref="NamespaceFixer.TryFixNamespaceAsync"/>. Degraded = the file was moved
/// but the namespace was NOT rewritten (a precondition failed); Failed = the namespace WAS
/// rewritten but reference verification could not confirm everything resolves.
/// </summary>
public enum NsFixOutcome { Fixed, Unchanged, Degraded, Failed }

/// <summary>
/// Result of an attempted namespace fix. <see cref="Message"/> holds the degradation reason
/// (Degraded) or the failure detail (Failed); it is null for Fixed/Unchanged.
/// </summary>
public readonly record struct NsFixResult(
    NsFixOutcome Outcome, string? OldNamespace = null, string? NewNamespace = null,
    int RefsUpdated = 0, string? Message = null)
{
    public static NsFixResult Degraded(string reason) => new(NsFixOutcome.Degraded, Message: reason);
    public static NsFixResult Failed(string message) => new(NsFixOutcome.Failed, Message: message);
    public static NsFixResult Unchanged(string ns) => new(NsFixOutcome.Unchanged, OldNamespace: ns);
    public static NsFixResult Fixed(string oldNs, string newNs, int refsUpdated) =>
        new(NsFixOutcome.Fixed, oldNs, newNs, refsUpdated);
}

/// <summary>
/// Workflow that rewrites a moved C# file's namespace declaration to match its new path's IDE
/// convention (or an explicit override), then uses the warm LSP daemon's <c>diag</c> pull to find
/// referencing files that no longer resolve the moved types and patches them with a `using`
/// directive for the new namespace.
///
/// Every precondition (lsp module enabled, workspace/project resolvable, a namespace declaration
/// is present, the current namespace matches the old path's convention) is checked BEFORE any file
/// is touched — if one fails, this returns <see cref="NsFixOutcome.Degraded"/> and the caller falls
/// back to the plain move-only behavior with an explanatory note. The move itself is never affected.
///
/// Once past those checks the declaration is rewritten — from that point on, failures (a timed-out
/// or failing diag call, unresolved references after patching) are reported as
/// <see cref="NsFixOutcome.Failed"/> rather than silently degraded, since the file's namespace has
/// already changed and needs the user's attention.
/// </summary>
public static class NamespaceFixer
{
    /// <summary>
    /// Attempts to rewrite the moved file's namespace declaration to match its new path's IDE
    /// convention (or an explicit <paramref name="nsOverride"/>), then patches referencing files.
    /// </summary>
    public static async Task<NsFixResult> TryFixNamespaceAsync(string oldPath, string newPath, string? nsOverride)
    {
        var lspModule = ModuleCatalog.All.First(m => m.Name == "lsp");
        if (!ModuleConfig.Load().IsEnabled(lspModule))
            return NsFixResult.Degraded("lsp module disabled");

        var workspaceRoot = LspCommandHelpers.ResolveWorkspaceRoot();
        if (workspaceRoot is null)
            return NsFixResult.Degraded("could not resolve workspace root (.sln/.csproj)");

        var oldProjectCsproj = NamespaceConvention.FindOwningProject(Path.GetDirectoryName(oldPath)!);
        if (oldProjectCsproj is null)
            return NsFixResult.Degraded("could not find an owning .csproj for the moved file's original location");

        var oldProjectDir = Path.GetDirectoryName(oldProjectCsproj)!;
        var oldRootNamespace = NamespaceConvention.ResolveRootNamespace(
            await File.ReadAllTextAsync(oldProjectCsproj).ConfigureAwait(false), oldProjectCsproj);
        var expectedOldNamespace = NamespaceConvention.ComputeExpectedNamespace(oldRootNamespace, oldProjectDir, oldPath);

        var movedText = await File.ReadAllTextAsync(newPath).ConfigureAwait(false);
        var decl = NamespaceConvention.ParseDeclaration(movedText);
        if (decl is null)
            return NsFixResult.Degraded("no namespace declaration found in the moved file");

        var actualNamespace = decl.Value.Name;

        string targetNamespace;
        if (nsOverride is not null)
        {
            targetNamespace = nsOverride;
        }
        else if (actualNamespace != expectedOldNamespace)
        {
            return NsFixResult.Degraded(
                $"current namespace '{actualNamespace}' doesn't match path convention " +
                $"'{expectedOldNamespace}' — pass --ns <target-namespace> to set it explicitly");
        }
        else
        {
            var newProjectCsproj = NamespaceConvention.FindOwningProject(Path.GetDirectoryName(newPath)!) ?? oldProjectCsproj;
            var newProjectDir = Path.GetDirectoryName(newProjectCsproj)!;
            var newRootNamespace = newProjectCsproj == oldProjectCsproj
                ? oldRootNamespace
                : NamespaceConvention.ResolveRootNamespace(
                    await File.ReadAllTextAsync(newProjectCsproj).ConfigureAwait(false), newProjectCsproj);
            targetNamespace = NamespaceConvention.ComputeExpectedNamespace(newRootNamespace, newProjectDir, newPath);
        }

        if (targetNamespace == actualNamespace)
            return NsFixResult.Unchanged(actualNamespace);

        // Committed past this point: the declaration is about to change.
        var rewritten = NamespaceConvention.RewriteDeclaration(movedText, targetNamespace);
        await File.WriteAllTextAsync(newPath, rewritten).ConfigureAwait(false);

        var newProjectDirForScope = Path.GetDirectoryName(
            NamespaceConvention.FindOwningProject(Path.GetDirectoryName(newPath)!) ?? oldProjectCsproj)!;
        var (candidateFiles, _) = DiagCommand.ResolveScope(newProjectDirForScope, DiagCommand.DefaultMaxFiles);
        if (!candidateFiles.Contains(newPath, StringComparer.OrdinalIgnoreCase))
            candidateFiles.Add(newPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var scopeFiles = await NarrowScopeToReferencingFilesAsync(
                workspaceRoot, newPath, candidateFiles, cts.Token).ConfigureAwait(false);

            var (editedFiles, patchError) = await PatchBrokenReferencesAsync(
                workspaceRoot, scopeFiles, newPath, actualNamespace, targetNamespace, cts.Token).ConfigureAwait(false);
            if (patchError is not null)
            {
                return NsFixResult.Failed(
                    $"tk mv: moved and rewrote namespace to '{targetNamespace}', but diag verification failed: " +
                    $"{patchError} — run `tk diag` manually to check for broken references.");
            }

            var remaining = await FindUnresolvedDiagnosticsAsync(workspaceRoot, scopeFiles, cts.Token).ConfigureAwait(false);
            if (remaining.Count > 0)
            {
                var detail = string.Join('\n', remaining.Select(r => $"  {r}"));
                return NsFixResult.Failed(
                    $"tk mv: moved and rewrote namespace to '{targetNamespace}' (refs-updated={editedFiles!.Count}), " +
                    $"but {remaining.Count} reference(s) still don't resolve — fix these by hand:\n{detail}");
            }

            return NsFixResult.Fixed(actualNamespace, targetNamespace, editedFiles!.Count);
        }
        catch (OperationCanceledException)
        {
            return NsFixResult.Failed(
                $"tk mv: moved and rewrote namespace to '{targetNamespace}', but timed out verifying " +
                "references via the LSP daemon — run `tk diag` manually.");
        }
    }

    /// <summary>
    /// Narrows <paramref name="candidateFiles"/> (the full project-scope file list) down to
    /// files that could plausibly reference the moved file's declared symbols, so the
    /// expensive per-file diag pull doesn't have to touch every file in the project. Fetches
    /// the moved file's outline and keeps any candidate file containing at least one declared
    /// name as a whole word (a cheap local text scan — no further daemon round trips), plus the
    /// moved file itself unconditionally. Find-references is deliberately not used here: by
    /// this point the moved file's namespace has already been rewritten, so extension-method
    /// call sites in files still missing the new `using` can no longer bind semantically and
    /// would be invisible to a reference search. If the outline call fails, falls back to the
    /// full, unfiltered <paramref name="candidateFiles"/> list so a transient failure can't
    /// cause a real broken reference to be missed.
    /// </summary>
    internal static async Task<List<string>> NarrowScopeToReferencingFilesAsync(
        string workspaceRoot, string movedFilePath, List<string> candidateFiles, CancellationToken ct)
    {
        var request = new DaemonRequest("outline", movedFilePath, 0, 0, null);
        var resp = await DaemonClient.SendAsync(workspaceRoot, request, ct).ConfigureAwait(false);
        if (!resp.Success || resp.Outline is null)
            return candidateFiles;

        var names = CollectDeclaredNames(resp.Outline);
        if (names.Count == 0)
            return candidateFiles;

        return await FilterCandidatesByNamesAsync(candidateFiles, movedFilePath, names, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps a candidate file if it contains any of <paramref name="names"/> as a whole-word
    /// text match, or if it is <paramref name="movedFilePath"/> itself (always kept
    /// unconditionally). Pure text scan — no daemon involved — so it's directly testable.
    /// </summary>
    internal static async Task<List<string>> FilterCandidatesByNamesAsync(
        List<string> candidateFiles, string movedFilePath, HashSet<string> names, CancellationToken ct)
    {
        // \b must be inside the alternation (not applied externally after the fact): the regex
        // engine only tries the next alternative at a given start position if the current one
        // fails to match at all, so an external post-match boundary check would let a shorter
        // name that happens to be a prefix of a longer one (e.g. "Foo" vs "FooBar") consume the
        // match position and never let "FooBar" itself be tried there.
        var pattern = new Regex(
            @"\b(?:" + string.Join('|', names.Select(Regex.Escape)) + @")\b",
            RegexOptions.Compiled);

        var narrowed = new List<string>();
        foreach (var file in candidateFiles)
        {
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(movedFilePath), StringComparison.Ordinal))
            {
                narrowed.Add(file);
                continue;
            }

            var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            if (pattern.IsMatch(text))
                narrowed.Add(file);
        }

        return narrowed;
    }

    /// <summary>
    /// Flattens a <see cref="DocumentSymbolInfo"/> outline tree (walking <c>Children</c> too)
    /// into the set of distinct declared bare identifier names — types, methods, properties,
    /// nested members, etc. The server's <c>name</c> field for methods carries a signature
    /// suffix (e.g. <c>"Gross(Payment) : decimal"</c> rather than plain <c>"Gross"</c>), which
    /// would never text-match a real call site, so each name is trimmed down to its leading
    /// identifier run before being added. Namespace nodes (Kind falls through
    /// <see cref="LspResultParser.SymbolKindName"/>'s default, "symbol") are walked for their
    /// children but their own name is skipped: it's the file's root namespace segment (e.g.
    /// "Smoke" from "Smoke.New"), which would spuriously text-match every other file's own
    /// `namespace Smoke.*` declaration and defeat the narrowing entirely. May still include
    /// names irrelevant to reference-narrowing (e.g. private members); that only ever widens
    /// the candidate file list, never narrows it incorrectly.
    /// </summary>
    internal static HashSet<string> CollectDeclaredNames(DocumentSymbolInfo[] symbols)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Walk(DocumentSymbolInfo[] nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Kind != "symbol")
                {
                    var bareName = LeadingIdentifier(node.Name);
                    if (bareName.Length > 0)
                        names.Add(bareName);
                }
                if (node.Children is { Length: > 0 } children)
                    Walk(children);
            }
        }
        Walk(symbols);
        return names;
    }

    private static string LeadingIdentifier(string name)
    {
        var i = 0;
        while (i < name.Length && IsIdentifierChar(name[i]))
            i++;
        return name[..i];
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Pulls diagnostics for <paramref name="scopeFiles"/>, and for every file reporting a
    /// CS0246/CS0234 ("type or namespace not found") diagnostic, adds a `using` directive so it
    /// can resolve the moved type again: the moved file itself needs the type's OLD namespace
    /// (for sibling types it referenced implicitly before moving out of that namespace); every
    /// other affected file needs the NEW namespace. Returns the list of files actually edited, or
    /// an error message if the diag call itself failed.
    /// </summary>
    private static async Task<(List<string>? EditedFiles, string? Error)> PatchBrokenReferencesAsync(
        string workspaceRoot, List<string> scopeFiles, string movedFilePath,
        string oldNamespace, string newNamespace, CancellationToken ct)
    {
        var request = new DaemonRequest("diag", null, 0, 0, null, Paths: [.. scopeFiles]);
        var resp = await DaemonClient.SendAsync(workspaceRoot, request, ct).ConfigureAwait(false);
        if (!resp.Success)
            return (null, resp.Error ?? "unknown error");

        var editedFiles = new List<string>();
        foreach (var fd in resp.DiagnosticsByFile ?? [])
        {
            if (!fd.Diagnostics.Any(IsUnresolvedTypeOrNamespace))
                continue;

            var filePath = RenameFormatter.UriToPath(fd.Uri);
            var isMovedFile = string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(movedFilePath), StringComparison.Ordinal);
            var nsToAdd = isMovedFile ? oldNamespace : newNamespace;

            var text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            var patched = UsingInserter.AddUsingIfMissing(text, nsToAdd);
            if (patched == text)
                continue;

            await File.WriteAllTextAsync(filePath, patched, ct).ConfigureAwait(false);
            editedFiles.Add(filePath);
        }

        return (editedFiles, null);
    }

    /// <summary>
    /// Re-pulls diagnostics for <paramref name="scopeFiles"/> after patching and returns a
    /// formatted line per remaining CS0246/CS0234 diagnostic (empty if none remain).
    /// </summary>
    private static async Task<List<string>> FindUnresolvedDiagnosticsAsync(
        string workspaceRoot, List<string> scopeFiles, CancellationToken ct)
    {
        var request = new DaemonRequest("diag", null, 0, 0, null, Paths: [.. scopeFiles]);
        var resp = await DaemonClient.SendAsync(workspaceRoot, request, ct).ConfigureAwait(false);
        if (!resp.Success)
            return [$"diag re-check failed: {resp.Error ?? "unknown error"}"];

        var lines = new List<string>();
        foreach (var fd in resp.DiagnosticsByFile ?? [])
        {
            var filePath = RenameFormatter.UriToPath(fd.Uri);
            foreach (var d in fd.Diagnostics.Where(IsUnresolvedTypeOrNamespace))
                lines.Add($"{filePath}:{d.Line + 1} {d.Code}: {d.Message}");
        }
        return lines;
    }

    private static bool IsUnresolvedTypeOrNamespace(LspDiagnostic d) =>
        d.Code is "CS0246" or "CS0234";
}
