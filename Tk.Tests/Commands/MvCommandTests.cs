using Tk;
using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

public class MvCommandTests
{
    // ─── helpers ────────────────────────────────────────────────────────────

    private static async Task<(int ExitCode, string Output, string Error, FakeProcessRunner Runner)> RunAsync(
        string[] args,
        FakeProcessRunner runner)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var ctx = new CommandContext(args, DetailLevel.Default, raw: false, stdout, stderr, runner);
        var exit = await new MvCommand().RunAsync(ctx);
        return (exit, stdout.ToString(), stderr.ToString(), runner);
    }

    /// <summary>Creates a temp directory with a file inside and returns (dir, filePath).</summary>
    private static (string Dir, string FilePath) MakeTempFile(string name = "OldName.cs", string content = "")
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return (dir, path);
    }

    // ─── usage / validation ──────────────────────────────────────────────────

    [Fact]
    public async Task No_args_prints_usage_and_returns_1()
    {
        var runner = new FakeProcessRunner();
        var (exit, output, _, _) = await RunAsync([], runner);

        Assert.Equal(1, exit);
        Assert.Contains("usage: tk mv", output);
    }

    [Fact]
    public async Task One_arg_prints_usage_and_returns_1()
    {
        var runner = new FakeProcessRunner();
        var (exit, output, _, _) = await RunAsync(["foo.cs"], runner);

        Assert.Equal(1, exit);
        Assert.Contains("usage: tk mv", output);
    }

    [Fact]
    public async Task Missing_source_returns_1_with_error()
    {
        var runner = new FakeProcessRunner();
        var (exit, _, err, _) = await RunAsync(["/no/such/file.cs", "/tmp/dest.cs"], runner);

        Assert.Equal(1, exit);
        Assert.Contains("source not found", err);
    }

    [Fact]
    public async Task Directory_source_returns_1_with_unsupported_message()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var runner = new FakeProcessRunner();
        var (exit, _, err, _) = await RunAsync([dir, "/tmp/dest"], runner);

        Assert.Equal(1, exit);
        Assert.Contains("directory moves not supported yet", err);
    }

    // ─── git-tracked file → git mv ───────────────────────────────────────────

    [Fact]
    public async Task Git_tracked_file_invokes_git_mv()
    {
        var (dir, oldPath) = MakeTempFile();
        var newPath = Path.Combine(dir, "NewName.cs");

        var runner = new FakeProcessRunner()
            // git rev-parse --is-inside-work-tree → yes
            .Returns(exitCode: 0, stdout: "true")
            // git ls-files --error-unmatch → tracked
            .Returns(exitCode: 0, stdout: "")
            // git mv → success
            .Returns(exitCode: 0, stdout: "");

        var (exit, output, _, r) = await RunAsync([oldPath, newPath], runner);

        Assert.Equal(0, exit);
        Assert.Contains("git=yes", output);
        // Verify git mv was called with the right args
        Assert.Contains(r.Calls, c => c.Contains("mv") && c.Contains(oldPath) && c.Contains(newPath));
    }

    [Fact]
    public async Task Git_tracked_file_output_format_is_correct()
    {
        var (dir, oldPath) = MakeTempFile();
        var newPath = Path.Combine(dir, "NewName.cs");

        var runner = new FakeProcessRunner()
            .Returns(exitCode: 0, stdout: "true")
            .Returns(exitCode: 0, stdout: "")
            .Returns(exitCode: 0, stdout: "");

        var (exit, output, _, _) = await RunAsync([oldPath, newPath], runner);

        Assert.Equal(0, exit);
        // Output must be: mv <old> -> <new> git=yes
        Assert.Matches($@"^mv .+ -> .+ git=yes$", output.Trim());
    }

    // ─── untracked file → filesystem move ───────────────────────────────────

    [Fact]
    public async Task Untracked_file_uses_filesystem_move()
    {
        var (dir, oldPath) = MakeTempFile();
        var newPath = Path.Combine(dir, "NewName.cs");

        var runner = new FakeProcessRunner()
            // inside git work tree
            .Returns(exitCode: 0, stdout: "true")
            // NOT tracked (non-zero exit)
            .Returns(exitCode: 1, stdout: "");

        var (exit, output, _, _) = await RunAsync([oldPath, newPath], runner);

        Assert.Equal(0, exit);
        Assert.Contains("git=no", output);
        Assert.True(File.Exists(newPath), "file should have moved");
        Assert.False(File.Exists(oldPath), "old path should be gone");
    }

    [Fact]
    public async Task No_git_uses_filesystem_move()
    {
        var (dir, oldPath) = MakeTempFile();
        var newPath = Path.Combine(dir, "NewName.cs");

        var runner = new FakeProcessRunner()
            // Not inside a git work tree
            .Returns(exitCode: 1, stdout: "false");

        var (exit, output, _, _) = await RunAsync([oldPath, newPath], runner);

        Assert.Equal(0, exit);
        Assert.Contains("git=no", output);
        Assert.True(File.Exists(newPath));
        Assert.False(File.Exists(oldPath));
    }

    // ─── destination is a directory → move into it ──────────────────────────

    [Fact]
    public async Task New_is_existing_directory_moves_into_it()
    {
        var (dir, oldPath) = MakeTempFile("Original.txt");
        var destDir = Directory.CreateTempSubdirectory().FullName;
        var expectedDest = Path.Combine(destDir, "Original.txt");

        var runner = new FakeProcessRunner()
            .Returns(exitCode: 0, stdout: "true")
            .Returns(exitCode: 0, stdout: "")   // tracked
            .Returns(exitCode: 0, stdout: "");   // git mv success

        var (exit, output, _, r) = await RunAsync([oldPath, destDir], runner);

        Assert.Equal(0, exit);
        // git mv should have been called with the resolved destination
        Assert.Contains(r.Calls, c => c.Contains("mv") && c.Contains(expectedDest));
        Assert.Contains("->", output);
    }

    [Fact]
    public async Task New_is_existing_directory_filesystem_fallback_moves_into_it()
    {
        var (dir, oldPath) = MakeTempFile("File.txt");
        var destDir = Directory.CreateTempSubdirectory().FullName;
        var expectedDest = Path.Combine(destDir, "File.txt");

        var runner = new FakeProcessRunner()
            // not in git
            .Returns(exitCode: 1, stdout: "false");

        var (exit, _, _, _) = await RunAsync([oldPath, destDir], runner);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(expectedDest));
    }

    // ─── parent directory creation ───────────────────────────────────────────

    [Fact]
    public async Task Creates_missing_parent_directories()
    {
        var (_, oldPath) = MakeTempFile();
        var destDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "nested", "deep");
        var newPath = Path.Combine(destDir, "NewName.cs");

        var runner = new FakeProcessRunner()
            .Returns(exitCode: 1, stdout: "false"); // not in git

        var (exit, _, _, _) = await RunAsync([oldPath, newPath], runner);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(newPath));

        // clean up
        Directory.Delete(Path.GetDirectoryName(destDir)!, recursive: true);
    }

    // ─── FormatOutput pure logic ─────────────────────────────────────────────

    [Theory]
    [InlineData("old.cs", "new.cs", true, "mv old.cs -> new.cs git=yes")]
    [InlineData("old.cs", "new.cs", false, "mv old.cs -> new.cs git=no")]
    public void FormatOutput_returns_correct_string(
        string oldArg, string newArg, bool gitUsed, string expected)
    {
        var actual = MvCommand.FormatOutput(oldArg, newArg, gitUsed);
        Assert.Equal(expected, actual);
    }

    // ─── IsSamePath pure logic (case-sensitive same-path guard) ──────────────

    [Theory]
    [InlineData("Foo.cs", "foo.cs", false)]
    [InlineData("Foo.cs", "Foo.cs", true)]
    [InlineData("/a/b/Old.cs", "/a/b/old.cs", false)]
    [InlineData("/a/b/Old.cs", "/a/b/Old.cs", true)]
    public void IsSamePath_is_case_sensitive(string oldPath, string newPath, bool expected)
    {
        Assert.Equal(expected, MvCommand.IsSamePath(oldPath, newPath));
    }

    // ─── case-only rename is allowed (not blocked as "same path") ────────────

    [Fact]
    public async Task Case_only_rename_proceeds_via_git_mv()
    {
        var (dir, oldPath) = MakeTempFile("Foo.cs");
        var newPath = Path.Combine(dir, "foo.cs");

        var runner = new FakeProcessRunner()
            .Returns(exitCode: 0, stdout: "true")   // inside git work tree
            .Returns(exitCode: 0, stdout: "")       // tracked
            .Returns(exitCode: 0, stdout: "");      // git mv success

        var (exit, output, err, r) = await RunAsync([oldPath, newPath], runner);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("same", err);
        Assert.Contains("git=yes", output);
        Assert.Contains(r.Calls, c => c.Contains("mv") && c.Contains(oldPath) && c.Contains(newPath));
    }

    // ─── cross-directory namespace reminder ──────────────────────────────────

    [Fact]
    public void CrossDirectoryNote_null_for_same_directory_rename()
    {
        var note = MvCommand.CrossDirectoryNote(
            Path.Combine("a", "b", "Old.cs"),
            Path.Combine("a", "b", "New.cs"));
        Assert.Null(note);
    }

    [Fact]
    public void CrossDirectoryNote_present_when_directory_changes()
    {
        var note = MvCommand.CrossDirectoryNote(
            Path.Combine("a", "b", "Thing.cs"),
            Path.Combine("a", "c", "Thing.cs"));
        Assert.NotNull(note);
        Assert.Contains("namespace", note);
    }

    // ─── git mv failure propagates error ─────────────────────────────────────

    [Fact]
    public async Task Git_mv_failure_returns_1_with_error()
    {
        var (dir, oldPath) = MakeTempFile();
        var newPath = Path.Combine(dir, "NewName.cs");

        var runner = new FakeProcessRunner()
            .Returns(exitCode: 0, stdout: "true")
            .Returns(exitCode: 0, stdout: "")   // tracked
            .Returns(exitCode: 1, stdout: "", stderr: "fatal: bad source"); // git mv fails

        var (exit, _, err, _) = await RunAsync([oldPath, newPath], runner);

        Assert.Equal(1, exit);
        Assert.Contains("git mv failed", err);
    }
}
