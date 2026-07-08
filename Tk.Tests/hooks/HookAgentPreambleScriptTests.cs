using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit;

namespace Tk.Tests.Hooks;

/// <summary>
/// Wires the bash case-matrix test for hooks/tk-agent-preamble.sh (the PreToolUse hook
/// that appends a tk cheat-sheet to every Task-tool subagent prompt) into `dotnet test`.
/// All the actual assertions live in Tk.Tests/hooks/test-tk-agent-preamble.sh — a TAP-ish
/// script run against the real hook script with the real `jq` on PATH. This test just
/// shells out to it and surfaces its report on failure.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public class HookAgentPreambleScriptTests
{
    private static string ResolveScriptPath([CallerFilePath] string thisFile = "")
    {
        var hooksTestDir = Path.GetDirectoryName(thisFile)!;
        return Path.Combine(hooksTestDir, "test-tk-agent-preamble.sh");
    }

    [Fact]
    public async Task Hook_agent_preamble_case_matrix_passes()
    {
        var scriptPath = ResolveScriptPath();
        Assert.True(File.Exists(scriptPath), $"Test script not found at {scriptPath}");

        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            ArgumentList = { scriptPath },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start bash");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            throw new InvalidOperationException("test-tk-agent-preamble.sh timed out after 30s");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.True(proc.ExitCode == 0,
            $"test-tk-agent-preamble.sh reported failures (exit {proc.ExitCode}):\n{stdout}\n--- stderr ---\n{stderr}");
    }
}
