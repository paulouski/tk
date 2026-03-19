using Tk;
using Tk.Filters;

if (args.Length == 0 || args[0] is "--help" or "-h" or "--version")
{
    Console.WriteLine("tk - token killer for Claude Code");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  tk <command> [args...]     Run command with filtered output");
    Console.WriteLine("  tk log <file> [flags]      Read and filter ASP.NET service log");
    Console.WriteLine("  tk init                    Install Claude Code instructions to ~/.claude/CLAUDE.md");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  tk dotnet build|test|restore   .NET build output (NuGet dedup, CS grouping)");
    Console.WriteLine("  tk git status|log|diff|show    Git compact output");
    Console.WriteLine("  tk log <file>                  Filter service log (errors/warnings only)");
    Console.WriteLine("  tk log <file> --errors         Errors only (fail/crit)");
    Console.WriteLine("  tk log <file> --last 20        Last N entries");
    Console.WriteLine("  tk log <file> --all            No filtering, raw output");
    Console.WriteLine();
    Console.WriteLine("Any other command passes through unfiltered.");
    return 0;
}

// Built-in: tk init
if (args[0] == "init")
{
    return ClaudeInit.Run();
}

// Built-in: tk log <file> [flags]
if (args[0] == "log" && args.Length >= 2 && !args[1].StartsWith("-"))
{
    var filePath = args[1];
    var flags = args.Length > 2 ? args[2..] : [];
    Console.Write(LogFileFilter.Apply(filePath, flags));
    return 0;
}

var filter = FilterRegistry.Resolve(args);
var (exitCode, stdout, stderr) = await CommandRunner.RunAsync(args);
var raw = string.IsNullOrWhiteSpace(stderr)
    ? stdout
    : $"{stdout.TrimEnd()}\n{stderr}";
var filtered = filter.Apply(raw, exitCode);

Console.Write(filtered);
return exitCode;
