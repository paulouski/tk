namespace Tk.Commands;

public sealed class LsCommand : ICommand
{
    public string Name => "ls";

    public Task<int> RunAsync(CommandContext ctx)
    {
        var path = ctx.Args.LastOrDefault(a => !a.StartsWith('-')) ?? ".";

        if (!Directory.Exists(path))
        {
            if (File.Exists(path))
            {
                ctx.Out.WriteLine(Path.GetFileName(path));
                return Task.FromResult(0);
            }
            ctx.Err.WriteLine($"tk ls: {path}: no such file or directory");
            return Task.FromResult(1);
        }

        foreach (var entry in Directory.GetFileSystemEntries(path).Order())
        {
            var name = Path.GetFileName(entry);
            ctx.Out.WriteLine(Directory.Exists(entry) ? name + "/" : name);
        }
        return Task.FromResult(0);
    }
}
