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
var enabledModules = ModuleCatalog.All.Where(m => moduleConfig.IsEnabled(m)).ToList();
var registry = new BuiltinRegistry(enabledModules.SelectMany(m => m.Commands));

var sw = Stopwatch.StartNew();
var countingWriter = new CountingTextWriter(Console.Out);

if (registry.TryResolve(commandArgs[0], out var builtin))
{
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

var extCtx = CommandContext.FromCli(cliOptions, countingWriter, Console.Error);
var filter = cliOptions.Raw
    ? new PassthroughFilter()
    : FilterRegistry.Resolve(commandArgs, cliOptions.DetailLevel, enabledModules);
var execArgs = GrepArgsHelper.EnsureRecursive(commandArgs, Directory.Exists);
var exitCode = await OutputPipeline.RunAsync(execArgs, filter, extCtx);
sw.Stop();
Analytics.Record(commandArgs, exitCode, DetailString(cliOptions), sw.ElapsedMilliseconds,
    countingWriter.CharCount, countingWriter.Lines, extCtx.RawCharCount, extCtx.RawLineCount, extCtx.ResultCount);
return exitCode;

static void PrintHelp()
{
    Console.Write(HelpRenderer.BuildHelp(ModuleConfig.ResolveEnabled()));
}

static string DetailString(CliOptions opts) =>
    opts.Raw ? "raw" : opts.DetailLevel == DetailLevel.More ? "more" : "default";
