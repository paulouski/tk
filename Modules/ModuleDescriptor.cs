namespace Tk.Modules;

/// <summary>
/// Describes a named feature group that can be enabled or disabled at runtime.
/// <see cref="Rows"/> is the single declarative source for this module's commands: builtin
/// dispatch, external-filter resolution, and generated help all read from it.
/// </summary>
public sealed record ModuleDescriptor(
    string Name,
    IReadOnlyList<CommandRow> Rows,
    bool AlwaysOn,
    string? InitSnippet)
{
    /// <summary>Builtin command instances declared by this module's rows — the view consumed
    /// by dispatch (<see cref="Commands.BuiltinRegistry"/>) and by <c>tk module list</c>'s
    /// command count.</summary>
    public IReadOnlyList<Commands.ICommand> Commands { get; } =
        Rows.Where(r => r.Kind == CommandRowKind.Builtin).Select(r => r.BuiltinFactory!()).ToList();
}
