using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Tk.Tests.Integration;

/// <summary>
/// Publishes the real `tk` apphost once per test run (the same framework-dependent single-file
/// artifact <c>release.sh</c> ships — see <c>Tk.csproj</c>/<c>release.sh</c>) and hands every
/// test in the "Integration" collection its path, so the binary-spawn smoke tests exercise the
/// actual compiled+published executable rather than calling into Tk's code in-process.
///
/// Set <c>TK_TEST_BINARY=/path/to/tk</c> to skip publishing entirely and point the smoke tests
/// at an already-built binary (useful for local iteration or CI steps that publish separately).
///
/// macOS-targeted: publishing/running only makes sense on the platform these tests assert
/// process-spawn behavior for. On any other OS, <see cref="BinaryPath"/> is null and tests
/// short-circuit as a no-op (see <see cref="BinarySmokeTests"/>).
/// </summary>
public sealed class PublishedBinaryFixture : IDisposable
{
    /// <summary>Path to the published `tk` executable, or null if unavailable on this OS.</summary>
    public string? BinaryPath { get; }

    private readonly string? _ownedPublishDir;

    public PublishedBinaryFixture()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            BinaryPath = null;
            return;
        }

        var overridePath = Environment.GetEnvironmentVariable("TK_TEST_BINARY");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            BinaryPath = overridePath;
            return;
        }

        var repoRoot = ResolveRepoRoot();
        var csproj = Path.Combine(repoRoot, "Tk.csproj");
        if (!File.Exists(csproj))
            throw new InvalidOperationException($"Could not locate Tk.csproj at {csproj}");

        var outDir = Path.Combine(Path.GetTempPath(), $"tk-integration-publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        _ownedPublishDir = outDir;

        var rid = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList =
            {
                "publish", csproj,
                "-c", "Release",
                "-r", rid,
                "--self-contained", "false",
                "-p:PublishSingleFile=true",
                "-o", outDir,
            },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repoRoot,
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet publish");
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(TimeSpan.FromSeconds(120)))
        {
            proc.Kill(entireProcessTree: true);
            throw new InvalidOperationException("dotnet publish timed out after 120s");
        }

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet publish failed (exit {proc.ExitCode}):\n{stdout.Result}\n{stderr.Result}");
        }

        BinaryPath = Path.Combine(outDir, "tk");
        if (!File.Exists(BinaryPath))
            throw new InvalidOperationException($"Publish succeeded but binary not found at {BinaryPath}");
    }

    private static string ResolveRepoRoot([CallerFilePath] string thisFile = "")
    {
        // Tk.Tests/Integration/PublishedBinaryFixture.cs -> repo root (two levels up)
        var integrationDir = Path.GetDirectoryName(thisFile)!;
        var testsDir = Path.GetDirectoryName(integrationDir)!;
        return Path.GetDirectoryName(testsDir)!;
    }

    public void Dispose()
    {
        if (_ownedPublishDir is not null && Directory.Exists(_ownedPublishDir))
        {
            try { Directory.Delete(_ownedPublishDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}

[CollectionDefinition("Integration", DisableParallelization = true)]
public sealed class IntegrationCollection : ICollectionFixture<PublishedBinaryFixture>;
