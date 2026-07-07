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
/// A single compiler/analyzer diagnostic returned by a textDocument/diagnostic pull, in
/// 0-based LSP coordinates. Severity is one of "error", "warning", "info", "hint".
/// </summary>
public record LspDiagnostic(int Line, int Character, int EndLine, int EndChar, string Severity, string? Code, string Message);

/// <summary>
/// All diagnostics pulled for a single file (identified by its file:// URI).
/// </summary>
public record FileDiagnostics(string Uri, LspDiagnostic[] Diagnostics);

/// <summary>
/// Hover contents for a symbol at a position, returned by textDocument/hover. Contents is
/// the raw (markdown) hover text as the server returned it — markdown-fence/noise stripping
/// is a formatter concern (see SigFormatter), not parsed here.
/// </summary>
public record HoverResult(string Uri, int Line, int Character, string Contents);

/// <summary>
/// Outcome of a `tk fix` request: whether the restricted add/remove-using code-action flow
/// could be honored by the server at all (false when a fix would require a protocol
/// interaction — e.g. a mandatory workspace/executeCommand roundtrip — this daemon does not
/// implement), and how many using directives were added/removed when it was.
/// </summary>
public record FixSummary(bool Supported, int UsingsAdded, int UsingsRemoved, string? Note);

/// <summary>
/// Request sent to the LSP daemon over the unix socket. <see cref="Paths"/> is used only by
/// "diag", which can query multiple files (a project/directory scope) in a single round trip.
/// </summary>
public record DaemonRequest(string Method, string? FilePath, int Line, int Character, string? Symbol, string? NewName = null, string[]? Paths = null);

/// <summary>
/// Response from the LSP daemon.
/// </summary>
public record DaemonResponse(
    bool Success,
    string? Error,
    LspLocation[]? Locations,
    FileEdits[]? Edits = null,
    SymbolMatch[]? Candidates = null,
    CallerInfo[]? Callers = null,
    FileDiagnostics[]? DiagnosticsByFile = null,
    HoverResult? Hover = null,
    CallerInfo[]? Callees = null,
    FixSummary? Fix = null);

/// <summary>
/// The daemon's own process identity, persisted next to its socket: the daemon process's
/// PID and (once the language server has been launched) the PID of its Roslyn child.
/// Lets `lsp stop`/`lsp status` verify and, if necessary, forcibly terminate both even when
/// the socket is gone, stale, or unresponsive.
/// </summary>
public record DaemonPidInfo(int DaemonPid, int? ServerPid);

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

    /// <summary>
    /// Returns the pid-file path for the given workspace root.
    /// Path: &lt;tk-state-root&gt;/daemons/&lt;16-char-hash&gt;.pid
    /// </summary>
    public static string GetPidPath(string workspaceRoot)
    {
        var hash = ComputeHash(workspaceRoot);
        var dir = TkPaths.DaemonsDir();
        return Path.Combine(dir, $"{hash}.pid");
    }

    /// <summary>
    /// Writes the daemon's process identity to <paramref name="pidPath"/> as two lines
    /// (daemon pid, server pid). Best-effort: failures are swallowed since the pid file is
    /// a diagnostic/cleanup aid, not required for normal daemon operation.
    /// </summary>
    public static void WritePidInfo(string pidPath, DaemonPidInfo info)
    {
        try
        {
            File.WriteAllText(pidPath, $"{info.DaemonPid}\n{info.ServerPid?.ToString() ?? ""}\n");
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Reads a pid file written by <see cref="WritePidInfo"/>. Returns null if the file is
    /// missing, empty, or malformed.
    /// </summary>
    public static DaemonPidInfo? TryReadPidInfo(string pidPath)
    {
        try
        {
            if (!File.Exists(pidPath)) return null;
            var lines = File.ReadAllLines(pidPath);
            if (lines.Length == 0 || !int.TryParse(lines[0], out var daemonPid)) return null;
            int? serverPid = lines.Length > 1 && int.TryParse(lines[1], out var sp) ? sp : null;
            return new DaemonPidInfo(daemonPid, serverPid);
        }
        catch { return null; }
    }

    /// <summary>
    /// True if a process with the given PID exists and has not exited.
    /// </summary>
    public static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch { return false; }
    }

    /// <summary>
    /// Best-effort forceful kill of a process (and its descendants) by PID. No-op if the
    /// process no longer exists or is inaccessible.
    /// </summary>
    public static void TryKillProcessTree(int pid)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch { /* already gone or inaccessible */ }
    }

    private static string ComputeHash(string workspaceRoot) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspaceRoot)))[..16]
            .ToLowerInvariant();
}
