namespace Tk.Modules;

/// <summary>
/// Describes a named feature group that can be enabled or disabled at runtime.
/// </summary>
public sealed record ModuleDescriptor(
    string Name,
    IReadOnlyList<Commands.ICommand> Commands,
    bool AlwaysOn,
    string? InitSnippet);
