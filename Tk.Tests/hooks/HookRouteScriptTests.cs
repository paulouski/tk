using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit;

namespace Tk.Tests.Hooks;

/// <summary>
/// Wires the bash case-matrix test for hooks/tk-route.sh (the production PreToolUse router,
/// previously untested) into `dotnet test`. All the actual assertions live in
/// Tk.Tests/hooks/test-tk-route.sh — a TAP-ish script run against the real hook script in an
/// isolated HOME/PATH (fake rtk stub + real jq, and a jq-absent PATH variant). This test just
/// shells out to it and surfaces its report on failure.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public class HookRouteScriptTests
{
    private static string ResolveScriptPath([CallerFilePath] string thisFile = "")
    {
        var hooksTestDir = Path.GetDirectoryName(thisFile)!;
        return Path.Combine(hooksTestDir, "test-tk-route.sh");
    }

    [Fact]
    public async Task Hook_route_case_matrix_passes()
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
            throw new InvalidOperationException("test-tk-route.sh timed out after 30s");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.True(proc.ExitCode == 0,
            $"test-tk-route.sh reported failures (exit {proc.ExitCode}):\n{stdout}\n--- stderr ---\n{stderr}");
    }
}
