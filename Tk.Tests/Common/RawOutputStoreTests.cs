using Tk.Common;
using Xunit;

namespace Tk.Tests.Common;

/// <summary>
/// Covers the raw-store hygiene sweep added in phase D1: purge-on-write, never touching the
/// current run's own file, and swallowing cleanup failures. Uses <c>TK_RAW_DIR</c> to point the
/// store at a throwaway temp directory per test instead of the real <c>&lt;temp&gt;/tk-raw</c>,
/// so tests never interact with (or get polluted by) real raw copies from other tk runs.
/// </summary>
public class RawOutputStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _previousEnv;

    public RawOutputStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tk-raw-store-test-{Guid.NewGuid():N}");
        _previousEnv = Environment.GetEnvironmentVariable("TK_RAW_DIR");
        Environment.SetEnvironmentVariable("TK_RAW_DIR", _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TK_RAW_DIR", _previousEnv);
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ResolveDir_honors_TK_RAW_DIR_override()
    {
        Assert.Equal(_dir, RawOutputStore.ResolveDir());
    }

    [Fact]
    public void SaveIfNeeded_purges_files_older_than_seven_days()
    {
        Directory.CreateDirectory(_dir);
        var stale = Path.Combine(_dir, "stale-old-file.log");
        File.WriteAllText(stale, "old raw output");
        var staleTime = DateTime.UtcNow.AddDays(-8);
        File.SetLastWriteTimeUtc(stale, staleTime);

        var path = RawOutputStore.SaveIfNeeded("raw output", "filtered", ["dotnet", "test"], exitCode: 1, unparsedCount: 0);

        Assert.NotNull(path);
        Assert.False(File.Exists(stale), "a file older than 7 days must be purged on the next store write");
    }

    [Fact]
    public void SaveIfNeeded_does_not_purge_files_within_seven_days()
    {
        Directory.CreateDirectory(_dir);
        var recent = Path.Combine(_dir, "recent-file.log");
        File.WriteAllText(recent, "recent raw output");
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-1));

        RawOutputStore.SaveIfNeeded("raw output", "filtered", ["dotnet", "test"], exitCode: 1, unparsedCount: 0);

        Assert.True(File.Exists(recent), "a file within the 7-day window must survive the sweep");
    }

    [Fact]
    public void SaveIfNeeded_never_deletes_the_file_it_just_wrote()
    {
        var path = RawOutputStore.SaveIfNeeded("raw output", "filtered", ["dotnet", "test"], exitCode: 1, unparsedCount: 0);

        Assert.NotNull(path);
        Assert.True(File.Exists(path), "the current run's own just-written file must never be swept");
    }

    [Fact]
    public void SaveIfNeeded_still_succeeds_when_cleanup_cannot_delete_a_stale_file()
    {
        Directory.CreateDirectory(_dir);
        var undeletable = Path.Combine(_dir, "undeletable-old-file.log");
        File.WriteAllText(undeletable, "old raw output");
        File.SetLastWriteTimeUtc(undeletable, DateTime.UtcNow.AddDays(-30));

        // macOS: the user-immutable flag makes unlink() fail with EPERM regardless of directory
        // write permission — a deterministic, cross-machine way to force File.Delete to throw
        // for exactly this one entry, standing in for "a cleanup failure" (permission error,
        // locked file, etc.) without disturbing the rest of the directory.
        Chflags("uchg", undeletable);
        try
        {
            var path = RawOutputStore.SaveIfNeeded("raw output", "filtered", ["dotnet", "test"], exitCode: 1, unparsedCount: 0);

            Assert.NotNull(path);
            Assert.True(File.Exists(path), "the command's own save must still succeed despite a cleanup failure");
            Assert.True(File.Exists(undeletable), "the file the sweep couldn't delete is left in place, not thrown past");
        }
        finally
        {
            Chflags("nouchg", undeletable);
            File.Delete(undeletable);
        }
    }

    private static void Chflags(string flag, string path)
    {
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "chflags",
            ArgumentList = { flag, path },
            UseShellExecute = false,
        })!;
        proc.WaitForExit(5000);
    }
}
