using System.IO.Pipes;
using System.Text.Json;
using Tk.Lsp;
using Tk.Lsp.Protocol;
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
    public void DecideSyncAction_older_mtime_returns_Change()
    {
        // File replacement tools can preserve or backdate mtimes. Any difference from the
        // snapshot sent to Roslyn means the open document must be refreshed.
        var action = LspDaemon.DecideSyncAction(
            storedMtime: T1,
            fileExists: true,
            currentMtime: T0);

        Assert.Equal(LspDaemon.SyncAction.Change, action);
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

    [Fact]
    public async Task RefreshOpenDocuments_resyncs_changed_dependency()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tk-doc-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var dependencyPath = Path.Combine(root, "PayoutStatus.cs");
        var targetPath = Path.Combine(root, "TransferExportServiceTests.cs");
        var dependencyUri = new Uri(dependencyPath).ToString();
        var targetUri = new Uri(targetPath).ToString();

        try
        {
            await File.WriteAllTextAsync(dependencyPath, "enum PayoutTerminalStatus { Pending }");
            await File.WriteAllTextAsync(targetPath, "class TransferExportServiceTests { }");
            File.SetLastWriteTimeUtc(dependencyPath, T0);
            File.SetLastWriteTimeUtc(targetPath, T0);

            var (serverWrite, loopRead, serverRead, loopWrite) = MakePipes();
            await using var loop = new MessageLoop(loopRead, loopWrite);
            var sync = new DocumentSync(_ => { });

            await sync.EnsureFileOpenAsync(loop, dependencyPath, dependencyUri, CancellationToken.None);
            using (var openedDependency = await ReadNotificationAsync(serverRead))
                AssertNotification(openedDependency, "textDocument/didOpen", dependencyUri);

            await sync.EnsureFileOpenAsync(loop, targetPath, targetUri, CancellationToken.None);
            using (var openedTarget = await ReadNotificationAsync(serverRead))
                AssertNotification(openedTarget, "textDocument/didOpen", targetUri);

            await File.WriteAllTextAsync(dependencyPath, "enum PayoutStatus { Pending }");
            File.SetLastWriteTimeUtc(dependencyPath, T1);

            await sync.RefreshOpenDocumentsAsync(loop, CancellationToken.None);

            using (var closedDependency = await ReadNotificationAsync(serverRead))
                AssertNotification(closedDependency, "textDocument/didClose", dependencyUri);
            using (var reopenedDependency = await ReadNotificationAsync(serverRead))
            {
                AssertNotification(reopenedDependency, "textDocument/didOpen", dependencyUri);
                var text = reopenedDependency.RootElement.GetProperty("params")
                    .GetProperty("textDocument").GetProperty("text").GetString();
                Assert.Equal("enum PayoutStatus { Pending }", text);
            }

            serverWrite.Dispose();
            serverRead.Dispose();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    private static void AssertNotification(JsonDocument notification, string method, string uri)
    {
        Assert.Equal(method, notification.RootElement.GetProperty("method").GetString());
        Assert.Equal(uri, notification.RootElement.GetProperty("params")
            .GetProperty("textDocument").GetProperty("uri").GetString());
    }
}
