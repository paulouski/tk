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

        return ModuleConfig.ResolveEnabled();
    }

    public string Name => "init";

    private const string ClaudeMarker = "<!-- tk-global-claude -->";
    private const string AgentsMarker = "<!-- tk-global-agents -->";
    private const string OpencodeMarker = "<!-- tk-global-opencode -->";
    private const string EndMarker = "<!-- /tk-global -->";
    private const string CodexRulesMarker = "# tk-lsp-sandbox:start";
    private const string CodexRulesEndMarker = "# tk-lsp-sandbox:end";

    private const string CodexLspRules = """
        # tk-lsp-sandbox:start
        # Managed by `tk init`. Codex loads rules at startup.
        # https://learn.chatgpt.com/docs/agent-configuration/rules
        prefix_rule(
            pattern=["tk", ["def", "refs", "callers", "calls", "impl", "sig", "sym", "diag"]],
            decision="allow",
            justification="Read-only tk LSP commands need to run outside the Codex sandbox so the daemon can use its Unix socket.",
            match=["tk def Customer", "tk refs Customer", "tk diag --changed"],
            not_match=["tk fix Example.cs", "tk rename Example.cs:1:1 Renamed"],
        )
        prefix_rule(
            pattern=["tk", ["--more", "--raw"], ["def", "refs", "callers", "calls", "impl", "sig", "sym", "diag"]],
            decision="allow",
            justification="Verbose read-only tk LSP commands need the same daemon access as their compact forms.",
            match=["tk --more refs Customer", "tk --raw diag --changed"],
            not_match=["tk --more rename Example.cs:1:1 Renamed", "tk --raw dotnet test"],
        )
        prefix_rule(
            pattern=["tk", "lsp", ["status", "stop"]],
            decision="allow",
            justification="tk LSP lifecycle commands must probe and manage the daemon outside the Codex sandbox.",
            match=["tk lsp status", "tk lsp stop"],
        )
        prefix_rule(
            pattern=["tk", ["fix", "rename", "mv"]],
            decision="allow",
            justification="User-authorized tk workspace mutation commands may run outside the Codex sandbox without per-command approval.",
            match=["tk fix Example.cs", "tk rename Example.cs:1:1 Renamed", "tk mv Old.cs New.cs"],
        )
        prefix_rule(
            pattern=["tk", ["--more", "--raw"], ["fix", "rename", "mv"]],
            decision="allow",
            justification="Verbose user-authorized tk workspace mutation commands may run outside the Codex sandbox without per-command approval.",
            match=["tk --more fix Example.cs", "tk --raw rename Example.cs:1:1 Renamed", "tk --more mv Old.cs New.cs"],
        )
        # tk-lsp-sandbox:end
        """;

    private string BuildInstructions(string marker, Func<ModuleDescriptor, string?> snippetSelector)
    {
        var snippets = ResolveEnabledModules()
            .Select(snippetSelector)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        return $"""
            {marker}
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
                EndMarker,
                BuildInstructions(ClaudeMarker, m => m.InitSnippet),
                "Claude global instructions"),
            new InstallTarget(
                Path.Combine(home, ".codex", "AGENTS.md"),
                AgentsMarker,
                EndMarker,
                BuildInstructions(AgentsMarker, m => ModuleCatalog.GetAgentsSnippet(m)),
                "global AGENTS instructions"),
            new InstallTarget(
                Path.Combine(home, ".config", "opencode", "AGENTS.md"),
                OpencodeMarker,
                EndMarker,
                BuildInstructions(OpencodeMarker, m => ModuleCatalog.GetAgentsSnippet(m)),
                "opencode global instructions"),
            new InstallTarget(
                Path.Combine(home, ".codex", "rules", "tk-lsp.rules"),
                CodexRulesMarker,
                CodexRulesEndMarker,
                CodexLspRules,
                "Codex LSP sandbox rules")
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
            ctx.Out.WriteLine("tk agent integration installed.");
        else if (failures.Count < targets.Length)
            ctx.Out.WriteLine("tk agent integration installed with partial failures.");
        else
            ctx.Out.WriteLine("tk agent integration install failed.");

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
        var updated = ReplaceMarkedBlock(existing, target.Marker, target.EndMarker, target.Instructions.Trim());

        if (updated == existing)
        {
            @out.WriteLine($"tk instructions already up to date in {target.Path}");
            return;
        }

        File.WriteAllText(target.Path, updated);
        @out.WriteLine($"tk instructions updated in {target.Path}");
    }

    private static string ReplaceMarkedBlock(string content, string marker, string endMarker, string block)
    {
        var start = content.IndexOf(marker, StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = content.IndexOf(endMarker, start, StringComparison.Ordinal);
            if (end >= 0)
            {
                end += endMarker.Length;
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

    private sealed record InstallTarget(
        string Path,
        string Marker,
        string EndMarker,
        string Instructions,
        string Label);
}
