namespace Tk.Commands.View;

/// <summary>
/// A pluggable strategy that produces a <see cref="OutlineResult"/> for a file. The view
/// command probes providers in order (LSP first for <c>.cs</c>, regex always) and renders
/// whichever returns a non-null result; a provider returns <c>null</c> to opt out
/// (e.g. LSP module disabled, daemon unavailable, request timed out), and the caller falls
/// through to the next provider. Line numbers in <see cref="OutlineEntry.StartLine"/>/
/// <see cref="OutlineEntry.EndLine"/> are 1-based for display.
/// </summary>
internal interface IFileOutlineProvider
{
    bool CanHandle(string path);
    Task<OutlineResult?> GetOutlineAsync(string path, CancellationToken ct);
}

/// <summary>
/// Result of an outline query. <see cref="Source"/> is the implementation tag rendered as
/// <c>source=lsp</c> or <c>source=regex approx</c> on the first output line, so the user
/// knows whether the ranges came from a real semantic source or the approximate regex map.
/// </summary>
internal sealed record OutlineResult(string Source, List<OutlineEntry> Entries);

/// <summary>
/// One symbol in an outline. <see cref="StartLine"/>/<see cref="EndLine"/> are 1-based and
/// inclusive. <see cref="BodySize"/> is <c>EndLine - StartLine + 1</c> pre-computed for
/// rendering. <see cref="Detail"/> is the LSP signature (e.g. method parameter list) when
/// available, otherwise null. <see cref="Children"/> carries nested symbols (LSP path);
/// null/empty for the flat regex path.
/// </summary>
internal sealed record OutlineEntry(
    string Kind,
    string Name,
    int StartLine,
    int EndLine,
    int BodySize,
    string? Detail,
    List<OutlineEntry> Children);
