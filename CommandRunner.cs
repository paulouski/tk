using System.Diagnostics;

namespace Tk;

public static class CommandRunner
{
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string[] args)
    {
        var command = args[0];
        var arguments = args.Length > 1 ? string.Join(' ', args[1..].Select(EscapeArg)) : "";

        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

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

    private static string EscapeArg(string arg) =>
        arg.Contains(' ') || arg.Contains('"') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;
}
