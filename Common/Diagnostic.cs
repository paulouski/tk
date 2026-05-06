namespace Tk.Common;

public sealed record Diagnostic(
    string File,
    int Line,
    int Column,
    string Kind,
    string Code,
    string Message,
    string Project);
