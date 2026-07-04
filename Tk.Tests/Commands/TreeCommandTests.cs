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
}
