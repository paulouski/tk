using Tk;
using Tk.Filters;

var cliOptions = CliOptionsParser.Parse(args);
var commandArgs = cliOptions.CommandArgs;

if (commandArgs.Length == 0 || commandArgs[0] is "--help" or "-h" or "--version")
{
    Console.WriteLine("tk - token killer for Claude Code");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  tk <command> [args...]     Run command with filtered output");
    Console.WriteLine("  tk --more <command>        Same command, with extra detail");
    Console.WriteLine("  tk --raw <command>         Run command with no tk filtering");
    Console.WriteLine("  tk log <file> [flags]      Read and filter ASP.NET service log");
    Console.WriteLine("  tk view <file[:a-b]>       Compact file view for agents");
    Console.WriteLine("  tk changes                 Compact repo state card (status + diff)");
    Console.WriteLine("  tk tree [path]             Compact repo tree for agents");
    Console.WriteLine("  tk files [path]            Compact file inventory");
    Console.WriteLine("  tk focus <query> [path]    Code-first repo search with top files and samples");
    Console.WriteLine("  tk init                    Install global Claude + AGENTS instructions");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  tk ls <path>                   Names only (no perms/dates/sizes)");
    Console.WriteLine("  tk grep [flags] pat path       Grep summary: match count, top files, samples");
    Console.WriteLine("  tk find <path> [flags]         Find with path prefix stripped");
    Console.WriteLine("  tk view <file[:a-b]>           File card or exact numbered line range");
    Console.WriteLine("  tk changes                     Repo status card for agent startup");
    Console.WriteLine("  tk tree [path]                 Repo tree with compact depth and counts");
    Console.WriteLine("  tk files [path]                Key files and top directories");
    Console.WriteLine("  tk focus <query> [path]        Code-first search; use --docs/--all/--code-only");
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
if (commandArgs[0] == "init")
{
    return ClaudeInit.Run();
}

// Built-in: tk ls [path]
if (commandArgs[0] == "ls")
{
    var path = commandArgs.Skip(1).LastOrDefault(a => !a.StartsWith('-')) ?? ".";
    if (!Directory.Exists(path))
    {
        if (File.Exists(path))
        {
            Console.WriteLine(Path.GetFileName(path));
            return 0;
        }
        Console.Error.WriteLine($"tk ls: {path}: no such file or directory");
        return 1;
    }
    foreach (var entry in Directory.GetFileSystemEntries(path).Order())
    {
        var name = Path.GetFileName(entry);
        Console.WriteLine(Directory.Exists(entry) ? name + "/" : name);
    }
    return 0;
}

// Built-in: tk view <file[:a-b]> [--symbols]
if (commandArgs[0] == "view" && commandArgs.Length >= 2 && !commandArgs[1].StartsWith("-"))
{
    var target = commandArgs[1];
    var flags = commandArgs.Length > 2 ? commandArgs[2..] : [];
    Console.Write(FileView.Render(target, flags, cliOptions));
    return 0;
}

// Built-in: tk tree [path]
if (commandArgs[0] == "tree")
{
    Console.Write(RepoMap.RenderTree(commandArgs[1..], cliOptions));
    return 0;
}

// Built-in: tk files [path]
if (commandArgs[0] == "files")
{
    Console.Write(await RepoMap.RenderFilesAsync(commandArgs[1..], cliOptions));
    return 0;
}

// Built-in: tk changes
if (commandArgs[0] == "changes")
{
    var (changesExitCode, output) = await RepoChanges.RunAsync(cliOptions);
    Console.Write(output);
    return changesExitCode;
}

// Built-in: tk focus <query> [path]
if (commandArgs[0] == "focus" && commandArgs.Length >= 2)
{
    var query = commandArgs[1];
    var remaining = commandArgs[2..];
    var path = remaining.FirstOrDefault(a => !a.StartsWith('-')) ?? ".";
    var flags = remaining.Where(a => a.StartsWith('-')).ToArray();
    var (focusExitCode, output) = await RepoFocus.RunAsync(query, path, flags, cliOptions);
    Console.Write(output);
    return focusExitCode;
}

// Built-in: tk log <file> [flags]
if (commandArgs[0] == "log" && commandArgs.Length >= 2 && !commandArgs[1].StartsWith("-"))
{
    var filePath = commandArgs[1];
    var flags = commandArgs.Length > 2 ? commandArgs[2..] : [];
    if (cliOptions.Raw && !flags.Contains("--all"))
        flags = [.. flags, "--all"];
    Console.Write(LogFileFilter.Apply(filePath, flags));
    return 0;
}

var filter = cliOptions.Raw
    ? new PassthroughFilter()
    : FilterRegistry.Resolve(commandArgs, cliOptions.DetailLevel);
var (exitCode, stdout, stderr) = await CommandRunner.RunAsync(commandArgs);
var raw = string.IsNullOrWhiteSpace(stderr)
    ? stdout
    : $"{stdout.TrimEnd()}\n{stderr}";
var filtered = filter.Apply(raw, exitCode);

Console.Write(filtered);
return exitCode;
