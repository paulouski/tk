using Tk.Lsp.Protocol;

namespace Tk.Lsp;

/// <summary>
/// Abstraction over a language-specific LSP server backend.
/// </summary>
public interface ILanguageBackend
{
    string Name { get; }
    string[] FileExtensions { get; }

    /// <summary>Workspace root markers (e.g. ".sln", ".csproj").</summary>
    string[] WorkspaceMarkers { get; }

    /// <summary>Returns the path to the server executable, or null if not found.</summary>
    string? ResolveServer();

    string[] GetLaunchArgs(string serverPath);

    /// <summary>Returns true if the message signals that the workspace is ready for queries.</summary>
    bool IsReadySignal(LspIncoming msg);

    string InstallHint { get; }
}
