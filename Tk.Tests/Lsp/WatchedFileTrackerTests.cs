using System.IO.Pipes;
using System.Text.Json;
using Tk.Lsp;
using Tk.Lsp.Protocol;
using Xunit;

namespace Tk.Tests.Lsp;

/// <summary>
/// Tests for <see cref="WatchedFileTracker"/> — the workspace/didChangeWatchedFiles forwarding
/// that closes the gap DocumentSync's _openDocs-scoped refresh cannot: a dependency file the
/// daemon was never asked to open (a new type added by a parallel process, an external edit to
/// an un-queried file, a delete/rename) staying invisible to Roslyn's workspace.
/// </summary>
public class WatchedFileTrackerTests
{
    [Fact]
    public async Task New_file_never_opened_by_tk_produces_Created_notification()
    {
        var root = NewRoot();
        try
        {
            var tracker = new WatchedFileTracker(root, [".cs"], _ => { });
            try
            {
                var newPath = Path.Combine(root, "NewDependency.cs");
                await File.WriteAllTextAsync(newPath, "class NewDependency { }");

                await WaitForPendingAsync(tracker);

                var (serverWrite, loopRead, serverRead, loopWrite) = MakePipes();
                await using var loop = new MessageLoop(loopRead, loopWrite);

                await tracker.FlushAsync(loop, CancellationToken.None);

                using var notif = await ReadNotificationAsync(serverRead);
                var change = SingleChange(notif, newPath);
                Assert.Equal(1, change.GetProperty("type").GetInt32()); // Created

                serverWrite.Dispose();
                serverRead.Dispose();
            }
            finally
            {
                tracker.Dispose();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Deleted_file_never_opened_by_tk_produces_Deleted_notification()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "Dependency.cs");
            await File.WriteAllTextAsync(path, "class Dependency { }");

            var tracker = new WatchedFileTracker(root, [".cs"], _ => { });
            try
            {
                File.Delete(path);

                await WaitForPendingAsync(tracker);

                var (serverWrite, loopRead, serverRead, loopWrite) = MakePipes();
                await using var loop = new MessageLoop(loopRead, loopWrite);

                await tracker.FlushAsync(loop, CancellationToken.None);

                using var notif = await ReadNotificationAsync(serverRead);
                var change = SingleChange(notif, path);
                Assert.Equal(3, change.GetProperty("type").GetInt32()); // Deleted

                serverWrite.Dispose();
                serverRead.Dispose();
            }
            finally
            {
                tracker.Dispose();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Content_change_to_file_never_opened_by_tk_produces_Changed_notification()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "Dependency.cs");
            await File.WriteAllTextAsync(path, "enum PayoutStatus { Pending }");

            var tracker = new WatchedFileTracker(root, [".cs"], _ => { });
            try
            {
                await File.WriteAllTextAsync(path, "enum PayoutStatus { Pending, Settled }");

                await WaitForPendingAsync(tracker);

                var (serverWrite, loopRead, serverRead, loopWrite) = MakePipes();
                await using var loop = new MessageLoop(loopRead, loopWrite);

                await tracker.FlushAsync(loop, CancellationToken.None);

                using var notif = await ReadNotificationAsync(serverRead);
                var change = SingleChange(notif, path);
                // Changed (2) is the common case; some platforms' FileSystemWatcher reports a
                // plain content rewrite as Created (1) instead (e.g. an atomic
                // replace-on-write). Either way Roslyn is told to re-read the file, which is
                // what matters here — the exact FileSystemEventArgs classification isn't.
                Assert.Contains(change.GetProperty("type").GetInt32(), new[] { 1, 2 });

                serverWrite.Dispose();
                serverRead.Dispose();
            }
            finally
            {
                tracker.Dispose();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Concurrent_flush_does_not_duplicate_or_drop_the_same_event()
    {
        var root = NewRoot();
        try
        {
            var tracker = new WatchedFileTracker(root, [".cs"], _ => { });
            try
            {
                var newPath = Path.Combine(root, "NewDependency.cs");
                await File.WriteAllTextAsync(newPath, "class NewDependency { }");

                await WaitForPendingAsync(tracker);

                var (serverWrite, loopRead, serverRead, loopWrite) = MakePipes();
                await using var loop = new MessageLoop(loopRead, loopWrite);

                // Two requests refreshing at once must serialize on the same drain: exactly
                // one of them observes and sends the pending event, the other finds nothing
                // left to send.
                await Task.WhenAll(
                    tracker.FlushAsync(loop, CancellationToken.None),
                    tracker.FlushAsync(loop, CancellationToken.None));

                using var notif = await ReadNotificationAsync(serverRead);
                SingleChange(notif, newPath);

                var second = await TryReadNotificationAsync(serverRead, TimeSpan.FromMilliseconds(500));
                Assert.Null(second);

                serverWrite.Dispose();
                serverRead.Dispose();
            }
            finally
            {
                tracker.Dispose();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Event_for_same_path_arriving_after_snapshot_remains_pending()
    {
        var root = NewRoot();
        try
        {
            var tracker = new WatchedFileTracker(root, [".cs"], _ => { });
            try
            {
                var path = Path.Combine(root, "Dependency.cs");
                tracker.EnqueueForTest(path, WatcherChangeTypes.Changed);
                tracker.PendingSnapshotTakenForTest = () =>
                {
                    tracker.PendingSnapshotTakenForTest = null;
                    tracker.EnqueueForTest(path, WatcherChangeTypes.Changed);
                };

                var (serverWrite, loopRead, serverRead, loopWrite) = MakePipes();
                await using var loop = new MessageLoop(loopRead, loopWrite);

                await tracker.FlushAsync(loop, CancellationToken.None);

                Assert.Equal(1, tracker.PendingCountForTest);
                using (var firstNotif = await ReadNotificationAsync(serverRead))
                {
                    var firstChange = SingleChange(firstNotif, path);
                    Assert.Equal(2, firstChange.GetProperty("type").GetInt32()); // Changed
                }

                await tracker.FlushAsync(loop, CancellationToken.None);

                using var notif = await ReadNotificationAsync(serverRead);
                var change = SingleChange(notif, path);
                Assert.Equal(2, change.GetProperty("type").GetInt32()); // Changed
                Assert.Equal(0, tracker.PendingCountForTest);

                serverWrite.Dispose();
                serverRead.Dispose();
            }
            finally
            {
                tracker.Dispose();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task No_change_on_disk_sends_no_notification()
    {
        var root = NewRoot();
        try
        {
            var tracker = new WatchedFileTracker(root, [".cs"], _ => { });
            try
            {
                var (serverWrite, loopRead, serverRead, loopWrite) = MakePipes();
                await using var loop = new MessageLoop(loopRead, loopWrite);

                await tracker.FlushAsync(loop, CancellationToken.None);

                var result = await TryReadNotificationAsync(serverRead, TimeSpan.FromMilliseconds(500));
                Assert.Null(result);

                serverWrite.Dispose();
                serverRead.Dispose();
            }
            finally
            {
                tracker.Dispose();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Overflow_recovery_does_a_full_rescan_diff_instead_of_trusting_the_queue()
    {
        var root = NewRoot();
        try
        {
            var keepPath = Path.Combine(root, "Keep.cs");
            var deletePath = Path.Combine(root, "ToDelete.cs");
            await File.WriteAllTextAsync(keepPath, "class Keep { }");
            await File.WriteAllTextAsync(deletePath, "class ToDelete { }");

            var tracker = new WatchedFileTracker(root, [".cs"], _ => { });
            try
            {
                File.Delete(deletePath);
                var newPath = Path.Combine(root, "New.cs");
                await File.WriteAllTextAsync(newPath, "class New { }");

                // Simulate a watcher buffer overflow: whatever real events the OS watcher did
                // or didn't manage to queue for the operations above must not be trusted —
                // the flush is expected to fall back to a full rescan-and-diff instead.
                tracker.ForceOverflowForTest();

                var (serverWrite, loopRead, serverRead, loopWrite) = MakePipes();
                await using var loop = new MessageLoop(loopRead, loopWrite);

                await tracker.FlushAsync(loop, CancellationToken.None);

                using var notif = await ReadNotificationAsync(serverRead);
                var changes = notif.RootElement.GetProperty("params").GetProperty("changes")
                    .EnumerateArray().ToArray();

                var byUri = changes.ToDictionary(c => c.GetProperty("uri").GetString()!, c => c.GetProperty("type").GetInt32());

                Assert.Equal(3, byUri[new Uri(deletePath).ToString()]); // Deleted
                Assert.Equal(1, byUri[new Uri(newPath).ToString()]);    // Created
                // Overflow can't rule out a missed edit to a file that was already known, so
                // it must be reported as Changed even though this test never touched it.
                Assert.Equal(2, byUri[new Uri(keepPath).ToString()]);   // Changed

                serverWrite.Dispose();
                serverRead.Dispose();
            }
            finally
            {
                tracker.Dispose();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Overflow_recovery_uses_configured_extensions_when_initial_workspace_was_empty()
    {
        var root = NewRoot();
        try
        {
            var tracker = new WatchedFileTracker(root, [".cs", ".csproj", ".sln"], _ => { });
            try
            {
                var projectPath = Path.Combine(root, "NewProject.csproj");
                await File.WriteAllTextAsync(projectPath, "<Project />");
                tracker.ForceOverflowForTest();

                var (serverWrite, loopRead, serverRead, loopWrite) = MakePipes();
                await using var loop = new MessageLoop(loopRead, loopWrite);

                await tracker.FlushAsync(loop, CancellationToken.None);

                using var notif = await ReadNotificationAsync(serverRead);
                var change = SingleChange(notif, projectPath);
                Assert.Equal(1, change.GetProperty("type").GetInt32()); // Created

                serverWrite.Dispose();
                serverRead.Dispose();
            }
            finally
            {
                tracker.Dispose();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tk-watched-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WaitForPendingAsync(WatchedFileTracker tracker)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (tracker.PendingCountForTest > 0) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("watcher did not observe the file-system change in time");
    }

    private static JsonElement SingleChange(JsonDocument notif, string expectedPath)
    {
        Assert.Equal("workspace/didChangeWatchedFiles", notif.RootElement.GetProperty("method").GetString());
        var changes = notif.RootElement.GetProperty("params").GetProperty("changes").EnumerateArray().ToArray();
        var change = Assert.Single(changes);
        Assert.Equal(new Uri(expectedPath).ToString(), change.GetProperty("uri").GetString());
        return change;
    }

    private static (AnonymousPipeServerStream serverWrite, AnonymousPipeClientStream loopRead,
                    AnonymousPipeServerStream serverRead, AnonymousPipeClientStream loopWrite)
        MakePipes()
    {
        var serverWrite = new AnonymousPipeServerStream(PipeDirection.Out);
        var loopRead = new AnonymousPipeClientStream(PipeDirection.In, serverWrite.ClientSafePipeHandle);
        var serverRead = new AnonymousPipeServerStream(PipeDirection.In);
        var loopWrite = new AnonymousPipeClientStream(PipeDirection.Out, serverRead.ClientSafePipeHandle);
        return (serverWrite, loopRead, serverRead, loopWrite);
    }

    private static async Task<JsonDocument> ReadNotificationAsync(Stream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var frame = await LspFrame.ReadNextAsync(stream, cts.Token);
        Assert.NotNull(frame);
        return JsonDocument.Parse(frame!);
    }

    private static async Task<JsonDocument?> TryReadNotificationAsync(Stream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var frame = await LspFrame.ReadNextAsync(stream, cts.Token);
            return frame is null ? null : JsonDocument.Parse(frame);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
