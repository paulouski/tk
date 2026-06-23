using Tk.Modules;

namespace Tk.Commands;

public sealed class InitCommand : ICommand
{
    // When constructed with no arguments, enabled modules are resolved at run time from ModuleConfig.
    private readonly IReadOnlyList<ModuleDescriptor>? _enabledModules;

    public InitCommand() { }

    public InitCommand(IReadOnlyList<ModuleDescriptor> enabledModules)
    {
        _enabledModules = enabledModules;
    }

    private IReadOnlyList<ModuleDescriptor> ResolveEnabledModules()
    {
        if (_enabledModules is not null)
            return _enabledModules;

        var config = ModuleConfig.Load();
        return ModuleCatalog.All.Where(m => config.IsEnabled(m)).ToList();
    }

    public string Name => "init";

    private const string ClaudeMarker = "<!-- tk-global-claude -->";
    private const string AgentsMarker = "<!-- tk-global-agents -->";
    private const string EndMarker = "<!-- /tk-global -->";

    private string BuildClaudeInstructions()
    {
        var snippets = ResolveEnabledModules()
            .Select(m => m.InitSnippet)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        return $"""
            {ClaudeMarker}
            {string.Join("\n\n", snippets)}
            {EndMarker}
            """;
    }

    private string BuildAgentsInstructions()
    {
        var snippets = ResolveEnabledModules()
            .Select(m => ModuleCatalog.GetAgentsSnippet(m))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        return $"""
            {AgentsMarker}
            {string.Join("\n\n", snippets)}
            {EndMarker}
            """;
    }

    public Task<int> RunAsync(CommandContext ctx)
    {
        var home = ResolveHomeDirectory();

        var targets = new[]
        {
            new InstallTarget(
                Path.Combine(home, ".claude", "CLAUDE.md"),
                ClaudeMarker,
                BuildClaudeInstructions(),
                "Claude global instructions"),
            new InstallTarget(
                Path.Combine(home, ".codex", "AGENTS.md"),
                AgentsMarker,
                BuildAgentsInstructions(),
                "global AGENTS instructions")
        };

        var failures = new List<string>();
        foreach (var target in targets)
        {
            try
            {
                UpsertInstructions(target, ctx.Out);
            }
            catch (Exception ex)
            {
                failures.Add($"{target.Path}: {ex.Message}");
                ctx.Err.WriteLine($"tk init failed for {target.Path}: {ex.Message}");
            }
        }

        if (failures.Count == 0)
            ctx.Out.WriteLine("tk global instructions installed.");
        else if (failures.Count < targets.Length)
            ctx.Out.WriteLine("tk global instructions installed with partial failures.");
        else
            ctx.Out.WriteLine("tk global instructions install failed.");

        foreach (var target in targets)
            ctx.Out.WriteLine($"  {target.Label}: {target.Path}");

        return Task.FromResult(failures.Count == 0 ? 0 : 1);
    }

    private static void UpsertInstructions(InstallTarget target, TextWriter @out)
    {
        var directory = Path.GetDirectoryName(target.Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var existing = File.Exists(target.Path) ? File.ReadAllText(target.Path) : "";
        var updated = ReplaceMarkedBlock(existing, target.Marker, target.Instructions.Trim());

        if (updated == existing)
        {
            @out.WriteLine($"tk instructions already up to date in {target.Path}");
            return;
        }

        File.WriteAllText(target.Path, updated);
        @out.WriteLine($"tk instructions updated in {target.Path}");
    }

    private static string ReplaceMarkedBlock(string content, string marker, string block)
    {
        var start = content.IndexOf(marker, StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = content.IndexOf(EndMarker, start, StringComparison.Ordinal);
            if (end >= 0)
            {
                end += EndMarker.Length;
                var prefix = content[..start].TrimEnd();
                var suffix = content[end..].TrimStart('\r', '\n');
                return JoinSections(prefix, block, suffix);
            }
        }

        return JoinSections(content.TrimEnd(), block, "");
    }

    private static string JoinSections(string prefix, string block, string suffix)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(prefix)) parts.Add(prefix);
        parts.Add(block);
        if (!string.IsNullOrWhiteSpace(suffix)) parts.Add(suffix);
        return string.Join("\n\n", parts) + "\n";
    }

    private static string ResolveHomeDirectory()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home) && Path.IsPathRooted(home))
            return home;

        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile) && Path.IsPathRooted(userProfile))
            return userProfile;

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private sealed record InstallTarget(string Path, string Marker, string Instructions, string Label);
}
