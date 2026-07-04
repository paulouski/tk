using Tk.Modules;

namespace Tk.Filters;

/// <summary>
/// Resolves the <see cref="IOutputFilter"/> for an external (non-builtin) command by walking
/// the module catalog's external-filter rows. Module-gated: a row from a disabled module is
/// invisible here exactly as it is to help and builtin dispatch, so disabling a module (e.g.
/// `tk module disable dotnet`) also disables its external filter — the command then passes
/// through unfiltered, like any unrecognized command.
/// </summary>
public static class FilterRegistry
{
    public static IOutputFilter Resolve(string[] args, DetailLevel detailLevel, IReadOnlyList<ModuleDescriptor> enabledModules)
    {
        if (args.Length == 0)
            return new PassthroughFilter();

        var command = args[0].ToLowerInvariant();

        var row = enabledModules
            .SelectMany(m => m.Rows)
            .FirstOrDefault(r => r.Kind == CommandRowKind.ExternalFilter
                && r.Name.Equals(command, StringComparison.OrdinalIgnoreCase));

        return row?.ExternalResolve?.Invoke(args, detailLevel) ?? new PassthroughFilter();
    }

    /// <summary>First non-flag argument after the command name — the dotnet subcommand
    /// (build/test/restore/...). Internal so ModuleCatalog's external-row factories can share it.</summary>
    internal static string? FindDotnetSubcommand(string[] args)
    {
        foreach (var arg in args.Skip(1))
        {
            if (!arg.StartsWith('-'))
                return arg;
        }

        return null;
    }

    /// <summary>Finds the search pattern argument for a grep/rg invocation, skipping flags
    /// (including ones that take a value) and honoring `-e`/`--regexp` and `--`.</summary>
    internal static string? FindSearchPattern(string[] args, string command)
    {
        var endOfOptions = false;
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (!endOfOptions && arg == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (!endOfOptions && arg.StartsWith('-'))
            {
                if (SearchOptionIsPattern(arg) && !arg.Contains('=') && i + 1 < args.Length)
                    return args[i + 1];
                if (SearchOptionIsPattern(arg) && arg.Contains('='))
                    return arg[(arg.IndexOf('=') + 1)..];
                if (SearchOptionNeedsValue(arg, command) && !arg.Contains('='))
                    i++;
                continue;
            }

            return arg;
        }

        return null;
    }

    private static bool SearchOptionNeedsValue(string arg, string command) =>
        command == "rg"
            ? arg is "-g" or "--glob" or "-t" or "--type" or "-T" or "--type-not"
                or "-f" or "--file" or "-m" or "--max-count"
                or "-A" or "--after-context" or "-B" or "--before-context" or "-C" or "--context"
            : arg is "-f" or "-m" or "-A" or "-B" or "-C";

    private static bool SearchOptionIsPattern(string arg) =>
        arg is "-e" or "--regexp" || arg.StartsWith("--regexp=", StringComparison.Ordinal);
}
