using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

/// <summary>
/// Unit tests for the document freshness sync decision logic in LspDaemon.
/// Tests the pure DecideSyncAction helper without requiring a live LSP server.
/// </summary>
public class DocSyncTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = T0.AddSeconds(5);

    [Fact]
    public void DecideSyncAction_file_missing_returns_Close()
    {
        var action = LspDaemon.DecideSyncAction(
            storedMtime: T0,
            fileExists: false,
            currentMtime: default);

        Assert.Equal(LspDaemon.SyncAction.Close, action);
    }

    [Fact]
    public void DecideSyncAction_mtime_newer_returns_Change()
    {
        var action = LspDaemon.DecideSyncAction(
            storedMtime: T0,
            fileExists: true,
            currentMtime: T1);

        Assert.Equal(LspDaemon.SyncAction.Change, action);
    }

    [Fact]
    public void DecideSyncAction_same_mtime_returns_None()
    {
        var action = LspDaemon.DecideSyncAction(
            storedMtime: T0,
            fileExists: true,
            currentMtime: T0);

        Assert.Equal(LspDaemon.SyncAction.None, action);
    }

    [Fact]
    public void DecideSyncAction_older_mtime_returns_None()
    {
        // Stored mtime is newer than current — shouldn't happen in practice,
        // but the decision should be None (not Change).
        var action = LspDaemon.DecideSyncAction(
            storedMtime: T1,
            fileExists: true,
            currentMtime: T0);

        Assert.Equal(LspDaemon.SyncAction.None, action);
    }

    [Fact]
    public void DecideSyncAction_missing_file_takes_priority_over_mtime()
    {
        // Even if currentMtime looks newer, missing file wins → Close.
        var action = LspDaemon.DecideSyncAction(
            storedMtime: T0,
            fileExists: false,
            currentMtime: T1);

        Assert.Equal(LspDaemon.SyncAction.Close, action);
    }
}
