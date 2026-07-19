using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

/// <summary>
/// Integration tests against a real, disposable git repo: proves <c>tk mv</c> never mutates the
/// git index (no <c>git mv</c>/<c>git add</c>/anything index-writing), for both tracked and
/// untracked files. Git detecting the rename on its own via content similarity — unstaged — is
/// the expected outcome, not a regression.
/// </summary>
[Collection("HomeSensitive")]
public class MvCommandGitIntegrationTests
{
    private static async Task<(int ExitCode, string Stdout, string Stderr)> GitAsync(string repoDir, params string[] args) =>
        await ProcessRunner.Default.RunAsync(["git", "-C", repoDir, .. args]).ConfigureAwait(false);

    /// <summary>Creates a temp git repo with one committed, tracked file. Returns the repo dir.</summary>
    private static async Task<string> MakeRepoWithTrackedFileAsync(string relPath, string content)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var (initExit, _, initErr) = await GitAsync(dir, "init", "-q");
        Assert.Equal(0, initExit);

        var fullPath = Path.Combine(dir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);

        var (addExit, _, addErr) = await GitAsync(dir, "add", relPath);
        Assert.True(addExit == 0, addErr);
        var (commitExit, _, commitErr) = await GitAsync(
            dir, "-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-q", "-m", "init");
        Assert.True(commitExit == 0, commitErr);

        return dir;
    }

    private static async Task<int> RunMvAsync(string oldPath, string newPath, params string[] extraArgs)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var ctx = new CommandContext(
            [oldPath, newPath, .. extraArgs], DetailLevel.Default, raw: false, stdout, stderr, new FakeProcessRunner());
        return await new MvCommand().RunAsync(ctx);
    }

    /// <summary>Runs <paramref name="body"/> with $HOME pointed at a scratch dir whose enabled
    /// tk modules are exactly <paramref name="enabledModules"/> — mirrors the same helper in
    /// MvCommandTests, kept local here to avoid cross-file test coupling.</summary>
    private static async Task WithHomeModulesAsync(string[] enabledModules, Func<Task> body)
    {
        var origHome = Environment.GetEnvironmentVariable("HOME");
        var tempHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var stateDir = Path.Combine(tempHome, ".local", "state", "tk");
            Directory.CreateDirectory(stateDir);
            File.WriteAllLines(Path.Combine(stateDir, "modules"), enabledModules);
            Environment.SetEnvironmentVariable("HOME", tempHome);
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", origHome);
            Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public async Task Tracked_file_move_does_not_touch_the_git_index()
    {
        var repoDir = await MakeRepoWithTrackedFileAsync("Old.txt", "hello");
        try
        {
            var indexPath = Path.Combine(repoDir, ".git", "index");
            var mtimeBefore = File.GetLastWriteTimeUtc(indexPath);

            var oldPath = Path.Combine(repoDir, "Old.txt");
            var newPath = Path.Combine(repoDir, "New.txt");
            var exit = await RunMvAsync(oldPath, newPath);

            Assert.Equal(0, exit);
            Assert.Equal(mtimeBefore, File.GetLastWriteTimeUtc(indexPath));
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task Tracked_file_move_leaves_diff_cached_empty_before_and_after()
    {
        var repoDir = await MakeRepoWithTrackedFileAsync("Old.txt", "hello");
        try
        {
            var (_, cachedBefore, _) = await GitAsync(repoDir, "diff", "--cached", "--name-status");
            Assert.Equal("", cachedBefore);

            var oldPath = Path.Combine(repoDir, "Old.txt");
            var newPath = Path.Combine(repoDir, "New.txt");
            var exit = await RunMvAsync(oldPath, newPath);
            Assert.Equal(0, exit);

            var (_, cachedAfter, _) = await GitAsync(repoDir, "diff", "--cached", "--name-status");
            Assert.Equal(cachedBefore, cachedAfter);
            Assert.Equal("", cachedAfter);
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task Tracked_file_move_shows_unstaged_delete_and_add_in_git_status()
    {
        var repoDir = await MakeRepoWithTrackedFileAsync("Old.txt", "hello");
        try
        {
            var oldPath = Path.Combine(repoDir, "Old.txt");
            var newPath = Path.Combine(repoDir, "New.txt");
            var exit = await RunMvAsync(oldPath, newPath);
            Assert.Equal(0, exit);

            var (_, statusShort, _) = await GitAsync(repoDir, "status", "--short");
            // Nothing staged: worktree-only delete of the old path, untracked new path (or a
            // git-detected rename — git status --short can show "R  " only for staged renames,
            // so an unstaged move always shows as separate D/?? lines).
            Assert.Contains(" D Old.txt", statusShort);
            Assert.Contains("?? New.txt", statusShort);
            Assert.DoesNotContain("R  ", statusShort); // no staged rename
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task Untracked_file_move_in_git_repo_stays_untracked_and_unstaged()
    {
        var repoDir = await MakeRepoWithTrackedFileAsync("Committed.txt", "hello");
        try
        {
            var oldPath = Path.Combine(repoDir, "Untracked.txt");
            File.WriteAllText(oldPath, "scratch");
            var newPath = Path.Combine(repoDir, "UntrackedRenamed.txt");

            var exit = await RunMvAsync(oldPath, newPath);
            Assert.Equal(0, exit);
            Assert.True(File.Exists(newPath));
            Assert.False(File.Exists(oldPath));

            var (_, cached, _) = await GitAsync(repoDir, "diff", "--cached", "--name-status");
            Assert.Equal("", cached);

            var (_, statusShort, _) = await GitAsync(repoDir, "status", "--short");
            Assert.Contains("?? UntrackedRenamed.txt", statusShort);
            Assert.DoesNotContain("Untracked.txt\n", statusShort.Replace("UntrackedRenamed.txt", ""));
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task Cross_directory_cs_move_namespace_fix_attempt_leaves_nothing_staged()
    {
        // lsp module explicitly disabled here (deterministic, matches
        // Cross_directory_cs_move_with_lsp_disabled_degrades_to_move_only in MvCommandTests):
        // the namespace fix degrades to move-only. What this test proves is the git-index
        // invariant end to end for a cross-directory .cs move specifically, including whatever
        // the namespace-fix attempt itself touches (nothing, when degraded).
        await WithHomeModulesAsync(["dotnet", "unity"], async () =>
        {
            var repoDir = await MakeRepoWithTrackedFileAsync(
                Path.Combine("OldFolder", "Foo.cs"), "namespace Whatever;\n\npublic class Foo {}\n");
            try
            {
                var oldPath = Path.Combine(repoDir, "OldFolder", "Foo.cs");
                var newPath = Path.Combine(repoDir, "NewFolder", "Foo.cs");

                var exit = await RunMvAsync(oldPath, newPath);
                Assert.Equal(0, exit);
                Assert.True(File.Exists(newPath));

                var (_, cached, _) = await GitAsync(repoDir, "diff", "--cached", "--name-status");
                Assert.Equal("", cached);

                var (_, statusShort, _) = await GitAsync(repoDir, "status", "--short");
                Assert.Contains("OldFolder/Foo.cs", statusShort);
                // git collapses a wholly-untracked new directory to "?? NewFolder/" rather than
                // listing the file inside it — still proves nothing was staged.
                Assert.Contains("NewFolder/", statusShort);
                Assert.DoesNotContain("R  ", statusShort); // still no staged rename
            }
            finally
            {
                Directory.Delete(repoDir, recursive: true);
            }
        });
    }
}
