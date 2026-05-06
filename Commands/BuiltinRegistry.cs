namespace Tk.Commands;

/// <summary>
/// Lookup table from command name to the built-in <see cref="ICommand"/> handler.
/// Constructed once in Program.cs with the full set of registered commands.
/// </summary>
public sealed class BuiltinRegistry
{
    private readonly Dictionary<string, ICommand> _map;

    public BuiltinRegistry(IEnumerable<ICommand> commands)
    {
        _map = commands.ToDictionary(c => c.Name, StringComparer.Ordinal);
    }

    public bool TryResolve(string name, out ICommand command) =>
        _map.TryGetValue(name, out command!);

    public IEnumerable<string> Names => _map.Keys;
}
