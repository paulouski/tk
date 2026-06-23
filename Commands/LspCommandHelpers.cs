namespace Tk.Commands;

/// <summary>
/// Shared helpers for LSP-backed commands (refs, rename).
/// </summary>
internal static class LspCommandHelpers
{
    /// <summary>
    /// Attempts to parse a position argument of the form file:line:col.
    /// Lines and columns are 1-based on input; returned values are 0-based for LSP.
    /// Returns false if the argument is not a valid position specifier.
    /// </summary>
    internal static bool TryParsePosition(string arg, out string filePath, out int line, out int col)
    {
        filePath = "";
        line = 0;
        col = 0;

        // Format: file:line:col (e.g. /path/to/File.cs:10:5)
        var lastColon = arg.LastIndexOf(':');
        if (lastColon <= 0) return false;

        var beforeLastColon = arg[..lastColon];
        var secondLastColon = beforeLastColon.LastIndexOf(':');
        if (secondLastColon <= 0) return false;

        var fileCandidate = arg[..secondLastColon];
        var lineStr = arg[(secondLastColon + 1)..lastColon];
        var colStr = arg[(lastColon + 1)..];

        if (!int.TryParse(lineStr, out var l) || !int.TryParse(colStr, out var c))
            return false;

        filePath = fileCandidate;
        line = l - 1; // convert to 0-based
        col = c - 1;  // convert to 0-based
        return true;
    }

    /// <summary>
    /// Resolves the workspace root (.sln or .csproj directory) from the current directory.
    /// Returns null if no workspace root is found.
    /// </summary>
    internal static string? ResolveWorkspaceRoot()
    {
        var target = Common.DotnetWorkspaceResolver.FindTarget(Directory.GetCurrentDirectory());
        if (target is null)
            return null;
        return Path.GetDirectoryName(Path.GetFullPath(target));
    }
}
