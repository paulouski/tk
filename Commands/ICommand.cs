namespace Tk.Commands;

public interface ICommand
{
    string Name { get; }
    Task<int> RunAsync(CommandContext ctx);
}
