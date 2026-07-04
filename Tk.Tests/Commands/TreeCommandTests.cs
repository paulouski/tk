using Tk;
using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

public class TreeCommandTests
{
    private static async Task<string> RunAsync(string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var ctx = new CommandContext(args, DetailLevel.Default, raw: false, stdout, stderr, new FakeProcessRunner());
        await new TreeCommand().RunAsync(ctx);
        return stdout.ToString();
    }

    /// <summary>
    /// Builds: root/A/B/C, root/A/B/D, root/E (E has no children).
    /// At the default depth (2), B sits exactly at the cutoff: its own children
    /// (C, D) are real but must not be rendered, since we're not recursing further.
    /// </summary>
    private static string MakeTempTree()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(root, "A", "B", "C"));
        Directory.CreateDirectory(Path.Combine(root, "A", "B", "D"));
        Directory.CreateDirectory(Path.Combine(root, "E"));
        return root;
    }

    [Fact]
    public async Task Depth_truncated_directory_shows_true_child_count_with_marker()
    {
        var root = MakeTempTree();
        try
        {
            var output = await RunAsync([root]);

            // B is truncated: it has 2 real subdirectories (C, D) hidden by the depth cutoff.
            Assert.Contains("B/ d=2+ f=0", output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Non_truncated_directory_shows_real_count_without_marker()
    {
        var root = MakeTempTree();
        try
        {
            var output = await RunAsync([root]);

            // A is fully expanded within depth (contains exactly one real child: B).
            Assert.Contains("A/ d=1 f=0", output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Genuine_leaf_directory_still_shows_d0_without_marker()
    {
        var root = MakeTempTree();
        try
        {
            var output = await RunAsync([root]);

            // E has no subdirectories at all — d=0 here is truthful, not a truncation artifact.
            Assert.Contains("E/ d=0 f=0", output);
            Assert.DoesNotContain("E/ d=0+", output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Deeper_explicit_depth_removes_truncation_marker()
    {
        var root = MakeTempTree();
        try
        {
            var output = await RunAsync([root, "--depth", "4"]);

            // With enough depth, B's children are actually rendered, so no marker is needed.
            Assert.Contains("B/ d=2 f=0", output);
            Assert.DoesNotContain("B/ d=2+", output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── fs-edge policy (phase D1): permission-denied entries are kept diagnostics, not
    // failures — unless the ROOT itself is unreadable, which IS a hard error. ─────────────────

    [Fact]
    public async Task Permission_denied_subdirectory_is_kept_as_a_visible_diagnostic()
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
            var output = await RunAsync([root]);

            // The denied subdirectory is still visible (not dropped, not a crash) with an
            // explicit diagnostic marker; the readable sibling still renders normally.
            Assert.Contains("denied/ (permission denied)", output);
            Assert.Contains("ok/ d=0 f=1", output);
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
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var ctx = new CommandContext([root], DetailLevel.Default, raw: false, stdout, stderr, new FakeProcessRunner());
            var exitCode = await new TreeCommand().RunAsync(ctx);

            Assert.Equal(1, exitCode);
            Assert.Contains("permission denied", stdout.ToString());
        }
        finally
        {
            Chmod("755", root);
            Directory.Delete(root, recursive: true);
        }
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
}
