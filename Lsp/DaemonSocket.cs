using System.Security.Cryptography;
using System.Text;
using Tk.Common;

namespace Tk.Lsp;

/// <summary>
/// Location returned by a references query.
/// </summary>
public record LspLocation(string Uri, int StartLine, int StartChar, int EndLine, int EndChar);

/// <summary>
/// A single LSP TextEdit produced by a rename operation (0-based LSP positions).
/// </summary>
public record RenameTextEdit(int StartLine, int StartChar, int EndLine, int EndChar, string NewText);

/// <summary>
/// All edits for a single file produced by a rename operation.
/// </summary>
public record FileEdits(string Uri, RenameTextEdit[] Edits);

/// <summary>
/// A symbol returned by a workspace/symbol query during name resolution.
/// </summary>
public record SymbolMatch(string Name, string ContainerName, string Kind, LspLocation Location);

/// <summary>
/// A caller of a symbol returned by callHierarchy/incomingCalls.
/// Location is the caller symbol's own name position; CallSites are the ranges inside
/// the caller where the target is called.
/// </summary>
public record CallerInfo(string Name, string ContainerName, string Kind, LspLocation Location, LspLocation[] CallSites);

/// <summary>
/// Request sent to the LSP daemon over the unix socket.
/// </summary>
public record DaemonRequest(string Method, string? FilePath, int Line, int Character, string? Symbol, string? NewName = null);

/// <summary>
/// Response from the LSP daemon.
/// </summary>
public record DaemonResponse(bool Success, string? Error, LspLocation[]? Locations, FileEdits[]? Edits = null, SymbolMatch[]? Candidates = null, CallerInfo[]? Callers = null);

/// <summary>
/// Utilities for locating the per-workspace daemon socket and log file.
/// </summary>
public static class DaemonSocket
{
    /// <summary>
    /// Returns the unix socket path for the given workspace root.
    /// Path: &lt;tk-state-root&gt;/daemons/&lt;16-char-hash&gt;.sock
    /// </summary>
    public static string GetSocketPath(string workspaceRoot)
    {
        var hash = ComputeHash(workspaceRoot);
        var dir = TkPaths.DaemonsDir();
        return Path.Combine(dir, $"{hash}.sock");
    }

    /// <summary>
    /// Returns the log file path for the given workspace root.
    /// Path: &lt;tk-state-root&gt;/daemons/&lt;16-char-hash&gt;.log
    /// </summary>
    public static string GetLogPath(string workspaceRoot)
    {
        var hash = ComputeHash(workspaceRoot);
        var dir = TkPaths.DaemonsDir();
        return Path.Combine(dir, $"{hash}.log");
    }

    private static string ComputeHash(string workspaceRoot) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspaceRoot)))[..16]
            .ToLowerInvariant();
}
