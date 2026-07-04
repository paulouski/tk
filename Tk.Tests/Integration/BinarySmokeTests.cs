using System.Diagnostics;
using Xunit;

namespace Tk.Tests.Integration;

/// <summary>
/// Thin end-to-end seam: spawns the actual published `tk` executable (see
/// <see cref="PublishedBinaryFixture"/>) as a real child process and checks its exit code and
/// output shape for a handful of representative commands. Deliberately narrow — this is not a
/// broad E2E suite, just enough to catch "doesn't even start" / "wrong exit code" / "wrong shape"
/// regressions that no in-process unit test can see (argv parsing, single-file publish, real
/// process spawning of `git`/`dotnet` from within `tk`).
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public class BinarySmokeTests
{
    private readonly PublishedBinaryFixture _fixture;

    public BinarySmokeTests(PublishedBinaryFixture fixture)
    {
        _fixture = fixture;
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(string binaryPath, string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start tk");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(TimeSpan.FromSeconds(15)))
        {
            proc.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"tk {string.Join(' ', args)} timed out after 15s");
        }

        return (proc.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static string MakeScratchGitRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tk-smoke-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        RunGit(dir, "init", "-q");
        RunGit(dir, "config", "user.email", "smoke@example.com");
        RunGit(dir, "config", "user.name", "smoke");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello from the binary smoke test\n");
        return dir;
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(5000);
    }

    [Fact]
    public void Help_exits_zero_and_lists_commands()
    {
        if (_fixture.BinaryPath is null)
            return; // non-macOS: see PublishedBinaryFixture doc comment.

        var (exitCode, stdout, _) = Run(_fixture.BinaryPath, Path.GetTempPath(), "--help");

        Assert.Equal(0, exitCode);
        Assert.Contains("tk - token killer for Claude Code", stdout);
        Assert.Contains("tk view", stdout);
        Assert.Contains("tk git", stdout);
    }

    [Fact]
    public void View_a_repo_file_exits_zero_and_shows_line_count()
    {
        if (_fixture.BinaryPath is null)
            return;

        var repo = MakeScratchGitRepo();
        try
        {
            var (exitCode, stdout, _) = Run(_fixture.BinaryPath, repo, "view", "a.txt");

            Assert.Equal(0, exitCode);
            Assert.Contains("view a.txt lines=1", stdout);
            Assert.Contains("hello from the binary smoke test", stdout);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Git_status_in_scratch_repo_exits_zero_and_shows_untracked_file()
    {
        if (_fixture.BinaryPath is null)
            return;

        var repo = MakeScratchGitRepo();
        try
        {
            var (exitCode, stdout, _) = Run(_fixture.BinaryPath, repo, "git", "status");

            Assert.Equal(0, exitCode);
            Assert.Contains("status st=0 mod=0 untr=1", stdout);
            Assert.Contains("u:a.txt", stdout);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }
}
