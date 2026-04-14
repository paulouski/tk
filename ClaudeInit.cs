namespace Tk;

public static class ClaudeInit
{
    private const string ClaudeMarker = "<!-- tk-global-claude -->";
    private const string AgentsMarker = "<!-- tk-global-agents -->";
    private const string EndMarker = "<!-- /tk-global -->";

    private const string ClaudeInstructions = $"""
        {ClaudeMarker}
        ## tk Global Compact Workflow

        Prefer `tk` for noisy commands and repo exploration when exact raw output is not required.

        Use:
        - `tk changes`
        - `tk tree`
        - `tk files`
        - `tk focus <query> [path]`
        - `tk focus <query> [path] --files-only`
        - `tk view <file>`
        - `tk dotnet build|test|restore`
        - `tk git status|diff|log`
        - `tk log <file>`

        Escalate detail as:
        - default `tk ...`
        - `tk --more ...`
        - `tk --raw ...`

        Compact keys:
        - `p=projects`, `t=time`, `e=errors`, `w=warnings`, `i=info`
        - `nu=NuGet vulnerabilities`, `pass=passed`, `fail=failed`, `skip=skipped`
        - `st=staged`, `mod=modified`, `untr=untracked`, `f=files`, `d=directories`, `m=matches`
        - `n=count`, `top=top items`, `br=branch`, `up=up to date`, `file=file`

        If compact output is enough to choose the next step, do not rerun raw.
        If compact output is ambiguous, try `--more`.
        If exact original output is needed, use `--raw`.
        {EndMarker}
        """;

    private const string AgentsInstructions = $"""
        {AgentsMarker}
        # tk Global Agent Preset

        Use `tk` as the default compact interface for repo exploration, search, and noisy command output.

        Prefer:
        - `tk changes`
        - `tk tree`
        - `tk files`
        - `tk focus <query> [path]`
        - `tk focus <query> [path] --files-only`
        - `tk view <file>`
        - `tk dotnet build|test|restore`
        - `tk git status|diff|log`
        - `tk log <file>`

        Escalation order:
        1. `tk ...`
        2. `tk --more ...`
        3. `tk --raw ...`

        Stable compact keys:
        - `p`, `t`, `e`, `w`, `i`, `nu`
        - `pass`, `fail`, `skip`
        - `st`, `mod`, `untr`
        - `f`, `d`, `m`, `n`
        - `top`, `br`, `up`, `file`

        If compact output is sufficient to choose the next action, do not rerun the raw command.
        If `tk` emits a raw tail fallback, treat it as parser uncertainty and inspect more carefully.
        {EndMarker}
        """;

    public static int Run()
    {
        var home = ResolveHomeDirectory();

        var targets = new[]
        {
            new InstallTarget(
                Path.Combine(home, ".claude", "CLAUDE.md"),
                ClaudeMarker,
                ClaudeInstructions,
                "Claude global instructions"),
            new InstallTarget(
                Path.Combine(home, ".codex", "AGENTS.md"),
                AgentsMarker,
                AgentsInstructions,
                "global AGENTS instructions")
        };

        var failures = new List<string>();
        foreach (var target in targets)
        {
            try
            {
                UpsertInstructions(target);
            }
            catch (Exception ex)
            {
                failures.Add($"{target.Path}: {ex.Message}");
                Console.Error.WriteLine($"tk init failed for {target.Path}: {ex.Message}");
            }
        }

        if (failures.Count == 0)
            Console.WriteLine("tk global instructions installed.");
        else if (failures.Count < targets.Length)
            Console.WriteLine("tk global instructions installed with partial failures.");
        else
            Console.WriteLine("tk global instructions install failed.");

        foreach (var target in targets)
            Console.WriteLine($"  {target.Label}: {target.Path}");

        return failures.Count == 0 ? 0 : 1;
    }

    private static void UpsertInstructions(InstallTarget target)
    {
        var directory = Path.GetDirectoryName(target.Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var existing = File.Exists(target.Path) ? File.ReadAllText(target.Path) : "";
        var updated = ReplaceMarkedBlock(existing, target.Marker, target.Instructions.Trim());

        if (updated == existing)
        {
            Console.WriteLine($"tk instructions already up to date in {target.Path}");
            return;
        }

        File.WriteAllText(target.Path, updated);
        Console.WriteLine($"tk instructions updated in {target.Path}");
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
