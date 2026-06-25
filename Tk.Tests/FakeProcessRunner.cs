using Tk.Commands;

namespace Tk.Tests;

/// <summary>
/// Test double for <see cref="IProcessRunner"/>. Records calls and returns
/// pre-canned responses keyed by the first arg pair (command + first non-flag).
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<(int ExitCode, string Stdout, string Stderr)> _responses = [];
    private int _index;

    public List<string[]> Calls { get; } = [];

    public FakeProcessRunner Returns(int exitCode = 0, string stdout = "", string stderr = "")
    {
        _responses.Add((exitCode, stdout, stderr));
        return this;
    }

    public Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string[] args)
    {
        Calls.Add(args);
        if (_index >= _responses.Count)
            throw new InvalidOperationException(
                $"FakeProcessRunner: unexpected call #{_index + 1}: {string.Join(' ', args)}");
        return Task.FromResult(_responses[_index++]);
    }

    public Task<int> RunInteractiveAsync(string[] args)
    {
        Calls.Add(args);
        if (_index >= _responses.Count)
            throw new InvalidOperationException(
                $"FakeProcessRunner: unexpected call #{_index + 1}: {string.Join(' ', args)}");
        return Task.FromResult(_responses[_index++].ExitCode);
    }
}
