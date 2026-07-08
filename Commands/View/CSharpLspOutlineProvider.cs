using Tk.Lsp;
using Tk.Modules;

namespace Tk.Commands.View;

/// <summary>
/// LSP-backed outline provider for <c>.cs</c> files. Probes the warm LSP daemon via
/// <see cref="DaemonClient.SendAsync"/> with method <c>outline</c> and translates the
/// returned <see cref="DocumentSymbolInfo"/> tree into <see cref="OutlineEntry"/> entries
/// (LSP 0-based → display 1-based lines).
///
/// Returns <c>null</c> on every failure path so the view command falls through to the
/// regex provider without surfacing a UI error: the lsp module disabled, no workspace
/// root discoverable, daemon not running / spawn-failed, request timed out, or the daemon
/// returned a non-success response. The outline is a hint, never a hard dependency.
/// </summary>
internal sealed class CSharpLspOutlineProvider : IFileOutlineProvider
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public bool CanHandle(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public async Task<OutlineResult?> GetOutlineAsync(string path, CancellationToken ct)
    {
        // Module gating: the lsp module owns all daemon-backed features. If the user has
        // turned it off, we don't try to spawn the daemon at all.
        var lspModule = ModuleCatalog.All.FirstOrDefault(m => m.Name == "lsp");
        if (lspModule is null || !ModuleConfig.Load().IsEnabled(lspModule))
            return null;

        var workspaceRoot = LspCommandHelpers.ResolveWorkspaceRoot();
        if (workspaceRoot is null)
            return null;

        var request = new DaemonRequest("outline", Path.GetFullPath(path), 0, 0, null);

        DaemonResponse response;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);
        try
        {
            response = await DaemonClient.SendAsync(workspaceRoot, request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }

        if (!response.Success)
            return null;

        if (response.Outline is null)
            return new OutlineResult("lsp", []);

        return new OutlineResult("lsp", MapSymbols(response.Outline));
    }

    private static List<OutlineEntry> MapSymbols(DocumentSymbolInfo[] symbols)
    {
        var entries = new List<OutlineEntry>(symbols.Length);
        foreach (var s in symbols)
            entries.Add(MapOne(s));
        return entries;
    }

    private static OutlineEntry MapOne(DocumentSymbolInfo s)
    {
        // LSP is 0-based; the outline renders 1-based for humans. BodySize is pre-computed
        // for the renderer so the format string doesn't need to know the convention.
        var startLine = s.StartLine + 1;
        var endLine = Math.Max(startLine, s.EndLine + 1);
        var bodySize = endLine - startLine + 1;
        var children = s.Children is null ? [] : MapSymbols(s.Children);
        return new OutlineEntry(s.Kind, s.Name, startLine, endLine, bodySize, s.Detail, children);
    }
}
