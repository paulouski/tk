using Tk.Commands;
using Tk.Filters;

namespace Tk.Modules;

/// <summary>Whether a catalog row dispatches to an in-process <see cref="ICommand"/>, or
/// selects an <see cref="IOutputFilter"/> for an external process (e.g. `dotnet`, `grep`).</summary>
public enum CommandRowKind
{
    Builtin,
    ExternalFilter
}

/// <summary>
/// One row of the command catalog — the single declarative source for builtin dispatch,
/// external-filter resolution, generated help, and module gating (see ModuleCatalog).
///
/// <para><see cref="Usage"/>/<see cref="Description"/> are null for rows that intentionally
/// have no "Commands:" help line (e.g. <c>init</c>/<c>switch</c>, already documented in the
/// static Usage header; <c>rg</c>, which shares grep's help line; the hidden
/// <c>__lsp-daemon</c> internal command). <see cref="ExtraHelpLines"/> holds additional
/// (usage, description) pairs rendered directly below the primary line — e.g. the
/// position-argument variants of refs/def/callers, or log's extra flag variants.</para>
/// </summary>
public sealed record CommandRow(
    string Name,
    CommandRowKind Kind,
    Func<ICommand>? BuiltinFactory,
    Func<string[], DetailLevel, IOutputFilter>? ExternalResolve,
    string? Usage,
    string? Description,
    IReadOnlyList<(string Usage, string Description)>? ExtraHelpLines = null)
{
    public static CommandRow Builtin(
        ICommand instance,
        string? usage = null,
        string? description = null,
        IReadOnlyList<(string Usage, string Description)>? extraHelpLines = null) =>
        new(instance.Name, CommandRowKind.Builtin, () => instance, null, usage, description, extraHelpLines);

    public static CommandRow External(
        string name,
        Func<string[], DetailLevel, IOutputFilter> resolve,
        string? usage = null,
        string? description = null,
        IReadOnlyList<(string Usage, string Description)>? extraHelpLines = null) =>
        new(name, CommandRowKind.ExternalFilter, null, resolve, usage, description, extraHelpLines);
}
