using System.Reflection;
using Tk;
using Tk.Commands;
using Tk.Common;
using Tk.Filters;

var cliOptions = CliOptionsParser.Parse(args);
var commandArgs = cliOptions.CommandArgs;

if (commandArgs.Length == 0 || commandArgs[0] is "--help" or "-h")
{
    PrintHelp();
    return 0;
}

if (commandArgs[0] is "--version")
{
    var version = Assembly.GetExecutingAssembly().GetName().Version;
    Console.WriteLine($"tk {version?.Major}.{version?.Minor}.{version?.Build}");
    return 0;
}

var registry = new BuiltinRegistry([
    new LsCommand(),
    new ViewCommand(),
    new InitCommand(),
    new LogCommand(),
    new TreeCommand(),
    new FilesCommand(),
    new ChangesCommand(),
    new BranchCommand(),
    new GitCommand(),
    new FocusCommand(),
    new QualityCommand(),
    new SwitchCommand(),
    new UnityCommand(),
]);

if (registry.TryResolve(commandArgs[0], out var builtin))
{
    var ctx = CommandContext.FromCli(cliOptions, Console.Out, Console.Error);
    return await builtin.RunAsync(ctx);
}

var filter = cliOptions.Raw
    ? new PassthroughFilter()
    : FilterRegistry.Resolve(commandArgs, cliOptions.DetailLevel);
var (exitCode, stdout, stderr) = await ProcessRunner.Default.RunAsync(commandArgs);
var raw = string.IsNullOrWhiteSpace(stderr)
    ? stdout
    : $"{stdout.TrimEnd()}\n{stderr}";
var filtered = filter.Apply(raw, exitCode);
if (exitCode != 0)
    filtered = RawOutputStore.AppendFailureReference(raw, filtered, commandArgs);

Console.Write(filtered);
return exitCode;

static void PrintHelp()
{
    Console.WriteLine("tk - token killer for Claude Code");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  tk <command> [args...]     Run command with filtered output");
    Console.WriteLine("  tk --more <command>        Same command, with extra detail");
    Console.WriteLine("  tk --raw <command>         Run command with no tk filtering");
    Console.WriteLine("  tk log <file> [flags]      Read and filter ASP.NET service log");
    Console.WriteLine("  tk view <file[:a-b|::sym]> Compact file view; multiple files, line range, or symbol");
    Console.WriteLine("  tk changes                 Compact repo state card (status + diff)");
    Console.WriteLine("  tk branch [base]           Branch state vs base (commits + diff)");
    Console.WriteLine("  tk tree [path]             Compact repo tree for agents");
    Console.WriteLine("  tk files [path]            Compact file inventory");
    Console.WriteLine("  tk focus <query> [path]    Code-first repo search with top files and samples");
    Console.WriteLine("  tk quality [path]          Fast local quality gate for agent changes");
    Console.WriteLine("  tk init                    Install global Claude + AGENTS instructions");
    Console.WriteLine("  tk switch                  Toggle between two Claude Code accounts");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  tk ls <path>                   Names only (no perms/dates/sizes)");
    Console.WriteLine("  tk grep [flags] pat path       Grep summary: match count, top files, samples");
    Console.WriteLine("  tk find <path> [flags]         Find with path prefix stripped");
    Console.WriteLine("  tk view <file[:a-b|::sym]>     File card, line range, or symbol body; multiple files allowed");
    Console.WriteLine("  tk changes                     Repo status card for agent startup");
    Console.WriteLine("  tk branch [base]               Branch vs base (auto: upstream/main/master)");
    Console.WriteLine("  tk tree [path]                 Repo tree with compact depth and counts; add --code");
    Console.WriteLine("  tk files [path]                Key files and top directories; add --code");
    Console.WriteLine("  tk focus <query> [path]        Code-first search; use --code/--docs/--all");
    Console.WriteLine("  tk quality [path]              Local analyzers + dotnet build + format check; add --test-filter");
    Console.WriteLine("  tk unity tree|files|status     Unity-aware variants: hides Library/Temp/meta files");
    Console.WriteLine("  tk dotnet build|test|restore   .NET build output (NuGet dedup, CS grouping)");
    Console.WriteLine("  tk git status|log|diff|show    Git compact output");
    Console.WriteLine("  tk log <file>                  Filter service log (errors/warnings only)");
    Console.WriteLine("  tk log <file> --errors         Errors only (fail/crit)");
    Console.WriteLine("  tk log <file> --last 20        Last N entries");
    Console.WriteLine("  tk log <file> --all            No filtering, raw output");
    Console.WriteLine();
    Console.WriteLine("Any other command passes through unfiltered.");
}
