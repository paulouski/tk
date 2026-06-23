using Tk.Modules;

namespace Tk.Commands;

public sealed class ModuleCommand : ICommand
{
    public string Name => "module";

    public Task<int> RunAsync(CommandContext ctx)
    {
        var sub = ctx.Args.Length > 0 ? ctx.Args[0] : null;

        return sub?.ToLowerInvariant() switch
        {
            "list" => Task.FromResult(RunList(ctx)),
            "enable" => Task.FromResult(RunEnable(ctx)),
            "disable" => Task.FromResult(RunDisable(ctx)),
            _ => Task.FromResult(Usage(ctx))
        };
    }

    private static int RunList(CommandContext ctx)
    {
        var config = ModuleConfig.Load();
        foreach (var module in ModuleCatalog.All)
        {
            var state = config.IsEnabled(module) ? "enabled" : "disabled";
            var alwaysOn = module.AlwaysOn ? " always-on" : "";
            ctx.Out.WriteLine($"module={module.Name} {state} cmds={module.Commands.Count}{alwaysOn}");
        }

        return 0;
    }

    private static int RunEnable(CommandContext ctx)
    {
        var moduleName = ctx.Args.Length > 1 ? ctx.Args[1] : null;
        if (string.IsNullOrEmpty(moduleName))
        {
            ctx.Err.WriteLine("tk module enable: module name required");
            return 1;
        }

        var known = ModuleCatalog.All.FirstOrDefault(
            m => m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            ctx.Err.WriteLine($"tk module enable: unknown module '{moduleName}'");
            return 1;
        }

        ModuleConfig.Enable(known.Name);
        ctx.Out.WriteLine($"module={known.Name} enabled");
        return 0;
    }

    private static int RunDisable(CommandContext ctx)
    {
        var moduleName = ctx.Args.Length > 1 ? ctx.Args[1] : null;
        if (string.IsNullOrEmpty(moduleName))
        {
            ctx.Err.WriteLine("tk module disable: module name required");
            return 1;
        }

        if (moduleName.Equals("core", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Err.WriteLine("tk module disable: cannot disable core module");
            return 1;
        }

        var known = ModuleCatalog.All.FirstOrDefault(
            m => m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            ctx.Err.WriteLine($"tk module disable: unknown module '{moduleName}'");
            return 1;
        }

        ModuleConfig.Disable(known.Name);
        ctx.Out.WriteLine($"module={known.Name} disabled");
        return 0;
    }

    private static int Usage(CommandContext ctx)
    {
        ctx.Out.WriteLine("module: list|enable <name>|disable <name>");
        return 0;
    }
}
