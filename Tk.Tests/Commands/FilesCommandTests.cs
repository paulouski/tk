using Tk;
using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

public class FilesCommandTests
{
    private static async Task<(string Output, int ExitCode)> RunAsync(string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var ctx = new CommandContext(args, DetailLevel.Default, raw: false, stdout, stderr, new FakeProcessRunner());
        var exitCode = await new FilesCommand().RunAsync(ctx);
        return (stdout.ToString(), exitCode);
    }

    private static void Chmod(string mode, string path)
    {
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "chmod",
            ArgumentList = { mode, path },
            UseShellExecute = false,
        })!;
        proc.WaitForExit(5000);
    }

    // ── fs-edge policy (phase D1): permission-denied entries are kept diagnostics, not
    // failures — unless the ROOT itself is unreadable, which IS a hard error. ─────────────────

    [Fact]
    public async Task Permission_denied_subdirectory_is_disclosed_via_err_count_not_a_crash()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var deniedDir = Path.Combine(root, "denied");
        var okDir = Path.Combine(root, "ok");
        Directory.CreateDirectory(deniedDir);
        Directory.CreateDirectory(okDir);
        File.WriteAllText(Path.Combine(deniedDir, "secret.txt"), "secret");
        File.WriteAllText(Path.Combine(okDir, "a.txt"), "hello");

        Chmod("000", deniedDir);
        try
        {
            var (output, exitCode) = await RunAsync([root]);

            Assert.Equal(0, exitCode);
            Assert.Contains("err=1", output);
            Assert.Contains("ok/a.txt", output);
            Assert.DoesNotContain("secret.txt", output);
        }
        finally
        {
            Chmod("755", deniedDir);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Permission_denied_root_is_a_hard_error()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        Chmod("000", root);
        try
        {
            var (output, exitCode) = await RunAsync([root]);

            Assert.Equal(1, exitCode);
            Assert.Contains("permission denied", output);
        }
        finally
        {
            Chmod("755", root);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task No_denied_entries_means_no_err_field()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        try
        {
            var (output, exitCode) = await RunAsync([root]);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("err=", output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
