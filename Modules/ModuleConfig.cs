namespace Tk.Modules;

/// <summary>
/// Reads and writes the set of enabled module names from
/// <c>~/.claude/tk/modules</c> (one name per line).
/// If the file does not exist, all modules are considered enabled.
/// The <c>core</c> module is always enabled regardless of config.
/// </summary>
public sealed class ModuleConfig
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "tk", "modules");

    private readonly HashSet<string> _enabled;
    private readonly bool _defaultAll;

    private ModuleConfig(HashSet<string> enabled, bool defaultAll)
    {
        _enabled = enabled;
        _defaultAll = defaultAll;
    }

    public static ModuleConfig Load() => Load(ConfigPath);

    public static ModuleConfig Load(string path)
    {
        if (!File.Exists(path))
            return new ModuleConfig([], defaultAll: true);

        var lines = File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new ModuleConfig(lines, defaultAll: false);
    }

    /// <summary>
    /// Returns true if the given module is enabled.
    /// <c>core</c> is always enabled. If no config file exists, all modules are enabled.
    /// </summary>
    public bool IsEnabled(ModuleDescriptor module)
    {
        if (module.AlwaysOn)
            return true;

        if (_defaultAll)
            return true;

        return _enabled.Contains(module.Name);
    }

    /// <summary>
    /// Enables the named module by adding it to the config file.
    /// </summary>
    public static void Enable(string moduleName) => Enable(moduleName, ConfigPath);

    public static void Enable(string moduleName, string path)
    {
        EnsureDirectory(path);
        var lines = File.Exists(path)
            ? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l)).ToList()
            : [];

        if (!lines.Contains(moduleName, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(moduleName);
            File.WriteAllLines(path, lines);
        }
    }

    /// <summary>
    /// Disables the named module by removing it from the config file.
    /// Returns false if the module is <c>core</c> (cannot be disabled).
    /// </summary>
    public static bool Disable(string moduleName) => Disable(moduleName, ConfigPath);

    public static bool Disable(string moduleName, string path)
    {
        if (moduleName.Equals("core", StringComparison.OrdinalIgnoreCase))
            return false;

        EnsureDirectory(path);

        // If file does not exist, materialise it with all known non-core modules enabled minus the one being disabled.
        if (!File.Exists(path))
        {
            var allNonCore = ModuleCatalog.All
                .Where(m => !m.AlwaysOn && !m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Name);
            File.WriteAllLines(path, allNonCore);
            return true;
        }

        var lines = File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l) && !l.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        File.WriteAllLines(path, lines);
        return true;
    }

    /// <summary>
    /// Returns the list of enabled modules from the default config path.
    /// Equivalent to <c>ModuleCatalog.All.Where(config.IsEnabled)</c>.
    /// </summary>
    public static IReadOnlyList<ModuleDescriptor> ResolveEnabled() =>
        ResolveEnabled(Load());

    /// <summary>
    /// Returns the list of enabled modules from the given config.
    /// </summary>
    public static IReadOnlyList<ModuleDescriptor> ResolveEnabled(ModuleConfig config) =>
        ModuleCatalog.All.Where(m => config.IsEnabled(m)).ToList();

    private static void EnsureDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
