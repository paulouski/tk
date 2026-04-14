using System.Diagnostics;

namespace Tk;

public static class CommandRunner
{
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string[] args)
    {
        var command = args[0];

        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args.Skip(1))
            psi.ArgumentList.Add(arg);

        // Force English output for dotnet CLI
        if (command.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start: {command}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
