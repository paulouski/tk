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
if (!cliOptions.Raw)
{
    var footer = HiddenLinesFooter.Format(
        HiddenLinesFooter.CountLines(raw),
        HiddenLinesFooter.CountLines(filtered),
        cliOptions.DetailLevel);
    if (footer is not null)
        filtered = filtered.EndsWith('\n') ? $"{filtered}{footer}\n" : $"{filtered}\n{footer}\n";
}
if (exitCode != 0)
    filtered = RawOutputStore.AppendFailureReference(raw, filtered, commandArgs);

Console.Write(filtered);
return exitCode;

static void PrintHelp()
{
    Console.Write(HelpRenderer.BuildHelp(ModuleConfig.ResolveEnabled()));
}
