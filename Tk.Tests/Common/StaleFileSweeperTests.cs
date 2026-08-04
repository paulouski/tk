using Tk.Common;
using Xunit;

namespace Tk.Tests.Common;

public class StaleFileSweeperTests : IDisposable
{
    private readonly string _dir;

    public StaleFileSweeperTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tk-sweep-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Write(string name, TimeSpan age)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void Deletes_only_files_older_than_max_age()
    {
        var stale = Write("stale.log", TimeSpan.FromDays(20));
        var fresh = Write("fresh.log", TimeSpan.FromDays(3));

        StaleFileSweeper.Sweep(_dir, TimeSpan.FromDays(14));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    /// <summary>
    /// The daemons directory mixes logs with a live daemon's .sock/.pid/.lock. Sweeping it with
    /// a pattern must never touch those, however old they look.
    /// </summary>
    [Fact]
    public void Pattern_confines_the_sweep_to_matching_files()
    {
        var oldLog = Write("a.log", TimeSpan.FromDays(20));
        var oldSock = Write("a.sock", TimeSpan.FromDays(20));
        var oldPid = Write("a.pid", TimeSpan.FromDays(20));

        StaleFileSweeper.Sweep(_dir, TimeSpan.FromDays(14), "*.log");

        Assert.False(File.Exists(oldLog));
        Assert.True(File.Exists(oldSock));
        Assert.True(File.Exists(oldPid));
    }

    [Fact]
    public void Missing_directory_is_not_an_error()
    {
        StaleFileSweeper.Sweep(Path.Combine(_dir, "nope"), TimeSpan.FromDays(14));
    }
}
