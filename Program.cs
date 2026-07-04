using System.Diagnostics;
using System.Reflection;
using Tk;
using Tk.Commands;
using Tk.Common;
using Tk.Filters;
using Tk.Modules;

var cliOptions = CliOptionsParser.Parse(args);
var commandArgs = cliOptions.CommandArgs;

if (commandArgs.Length == 0 || commandArgs[0] is "--help" or "-h" or "help")
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

var moduleConfig = ModuleConfig.Load();
var registry = new BuiltinRegistry(
    ModuleCatalog.All
        .Where(m => moduleConfig.IsEnabled(m))
        .SelectMany(m => m.Commands));

var sw = Stopwatch.StartNew();

if (registry.TryResolve(commandArgs[0], out var builtin))
{
    var countingWriter = new CountingTextWriter(Console.Out);
    var ctx = CommandContext.FromCli(cliOptions, countingWriter, Console.Error);
    var exit = await builtin.RunAsync(ctx);
    sw.Stop();
    Analytics.Record(commandArgs, exit, DetailString(cliOptions), sw.ElapsedMilliseconds,
        countingWriter.CharCount, countingWriter.Lines, ctx.RawCharCount, ctx.RawLineCount, ctx.ResultCount);
    return exit;
}

if (GrepArgsHelper.WantsOwnHelp(commandArgs))
{
    Console.Write(GrepArgsHelper.HelpText());
    return 0;
}

var filter = cliOptions.Raw
    ? new PassthroughFilter()
    : FilterRegistry.Resolve(commandArgs, cliOptions.DetailLevel);
var execArgs = GrepArgsHelper.EnsureRecursive(commandArgs, Directory.Exists);
var (exitCode, stdout, stderr) = await ProcessRunner.Default.RunAsync(execArgs);
var raw = string.IsNullOrWhiteSpace(stderr)
    ? stdout
    : $"{stdout.TrimEnd()}\n{stderr}";
var ledger = new UnitLedger();
var filtered = filter.Apply(raw, exitCode, ledger);
if (!cliOptions.Raw)
{
    var rawPath = RawOutputStore.SaveIfNeeded(raw, filtered, commandArgs, exitCode, ledger.UnparsedCount);
    var footer = OutputFooter.Format(
        HiddenLinesFooter.CountLines(raw),
        HiddenLinesFooter.CountLines(filtered),
        ledger.UnparsedCount,
        cliOptions.DetailLevel,
        rawPath);
    if (footer is not null)
        filtered = filtered.EndsWith('\n') ? $"{filtered}{footer}\n" : $"{filtered}\n{footer}\n";
}

Console.Write(filtered);
sw.Stop();
var (shownChars, shownLines, rawChars, rawLines) = Analytics.FromRawAndFiltered(raw, filtered);
Analytics.Record(commandArgs, exitCode, DetailString(cliOptions), sw.ElapsedMilliseconds,
    shownChars, shownLines, rawChars, rawLines);
return exitCode;

static void PrintHelp()
{
    Console.Write(HelpRenderer.BuildHelp(ModuleConfig.ResolveEnabled()));
}

static string DetailString(CliOptions opts) =>
    opts.Raw ? "raw" : opts.DetailLevel == DetailLevel.More ? "more" : "default";
