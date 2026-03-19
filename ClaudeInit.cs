namespace Tk;

public static class ClaudeInit
{
    private const string Marker = "<!-- tk-instructions -->";

    private const string Instructions = $"""
        {Marker}
        # Token Killer (tk)

        `tk` is a CLI proxy that filters noisy command output to save context tokens. Use `tk` for routine commands where filtered output is sufficient. If you need full unfiltered output (e.g. debugging a build issue, reading exact error details), call the command directly without `tk`.

        ### Commands

        ```bash
        # .NET -- NuGet dedup, CS warning grouping, compact summary (99% savings)
        tk dotnet build
        tk dotnet test
        tk dotnet restore

        # Git -- compact output (60-80% savings)
        tk git status
        tk git log
        tk git diff
        tk git show

        # Service logs -- strips startup noise, deduplicates, errors+warnings only (95%+ savings)
        tk log <file>              # filtered (default)
        tk log <file> --errors     # errors only
        tk log <file> --last 10    # last N entries
        tk log <file> --all        # raw (no filtering)
        ```

        Any other command passes through unfiltered: `tk some-command args...`
        <!-- /tk-instructions -->
        """;

    public static int Run()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claudeDir = Path.Combine(home, ".claude");
        var claudeMd = Path.Combine(claudeDir, "CLAUDE.md");

        // Check if already installed
        if (File.Exists(claudeMd))
        {
            var existing = File.ReadAllText(claudeMd);
            if (existing.Contains(Marker))
            {
                Console.WriteLine("tk instructions already present in " + claudeMd);
                return 0;
            }
        }

        // Ensure directory exists
        Directory.CreateDirectory(claudeDir);

        // Append instructions
        var content = File.Exists(claudeMd) ? File.ReadAllText(claudeMd) : "";
        var separator = content.Length > 0 && !content.EndsWith('\n') ? "\n\n" : "\n";
        if (content.Length == 0) separator = "";

        File.WriteAllText(claudeMd, content + separator + Instructions.Trim() + "\n");

        Console.WriteLine("tk instructions added to " + claudeMd);
        return 0;
    }
}
