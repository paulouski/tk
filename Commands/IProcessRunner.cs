namespace Tk.Commands;

public interface IProcessRunner
{
    Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string[] args);
    Task<int> RunInteractiveAsync(string[] args);
}
