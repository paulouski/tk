using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Tk.Lsp.Protocol;
using Xunit;

namespace Tk.Tests.Lsp;

public class MessageLoopTests
{
    /// <summary>
    /// Creates a pair of anonymous pipes.
    /// Returns (serverWrite, loopRead) and (loopWrite, serverRead).
    /// The "server" side writes to serverWrite and reads from serverRead.
    /// The loop reads from loopRead and writes to loopWrite.
    /// </summary>
    private static (AnonymousPipeServerStream serverWrite, AnonymousPipeClientStream loopRead,
                    AnonymousPipeServerStream serverRead, AnonymousPipeClientStream loopWrite)
        MakePipes()
    {
        var sw = new AnonymousPipeServerStream(PipeDirection.Out);
        var lr = new AnonymousPipeClientStream(PipeDirection.In, sw.ClientSafePipeHandle);
        var sr = new AnonymousPipeServerStream(PipeDirection.In);
        var lw = new AnonymousPipeClientStream(PipeDirection.Out, sr.ClientSafePipeHandle);
        return (sw, lr, sr, lw);
    }

    [Fact]
    public async Task Loop_answers_workspace_configuration_with_null_array()
    {
        var (serverWrite, loopRead, serverRead, loopWrite) = MakePipes();

        await using var loop = new MessageLoop(loopRead, loopWrite);

        loop.RegisterHandler("workspace/configuration", (id, _) =>
            Task.FromResult<object?>(new object?[] { null }));

        // Write a workspace/configuration request from "server" side
        var request = """{"jsonrpc":"2.0","id":1,"method":"workspace/configuration","params":{"items":[]}}""";
        var frameBytes = LspFrame.Encode(request);
        await serverWrite.WriteAsync(frameBytes);
        await serverWrite.FlushAsync();

        // Read the response the loop writes back
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var responseJson = await LspFrame.ReadNextAsync(serverRead, cts.Token);

        Assert.NotNull(responseJson);
        using var doc = JsonDocument.Parse(responseJson!);
        Assert.Equal(1, doc.RootElement.GetProperty("id").GetInt32());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(1, result.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, result[0].ValueKind);

        serverWrite.Dispose();
        serverRead.Dispose();
    }

    [Fact]
    public async Task Loop_answers_client_registerCapability_with_null()
    {
        var (serverWrite, loopRead, serverRead, loopWrite) = MakePipes();

        await using var loop = new MessageLoop(loopRead, loopWrite);

        loop.RegisterHandler("client/registerCapability", (id, _) =>
            Task.FromResult<object?>(null));

        var request = """{"jsonrpc":"2.0","id":2,"method":"client/registerCapability","params":{"registrations":[]}}""";
        await serverWrite.WriteAsync(LspFrame.Encode(request));
        await serverWrite.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var responseJson = await LspFrame.ReadNextAsync(serverRead, cts.Token);

        Assert.NotNull(responseJson);
        using var doc = JsonDocument.Parse(responseJson!);
        Assert.Equal(2, doc.RootElement.GetProperty("id").GetInt32());
        // result should be null (absent or null value)
        var hasResult = doc.RootElement.TryGetProperty("result", out var resultEl);
        Assert.True(!hasResult || resultEl.ValueKind == JsonValueKind.Null);

        serverWrite.Dispose();
        serverRead.Dispose();
    }

    [Fact]
    public async Task Loop_completes_pending_request_on_response()
    {
        var (serverWrite, loopRead, serverRead, loopWrite) = MakePipes();

        await using var loop = new MessageLoop(loopRead, loopWrite);

        // Start a request
        var pendingTask = loop.SendRequestAsync("initialize", new { }, CancellationToken.None);

        // Read the request the loop sent (so we know the id)
        using var outCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sentFrame = await LspFrame.ReadNextAsync(serverRead, outCts.Token);
        Assert.NotNull(sentFrame);
        using var reqDoc = JsonDocument.Parse(sentFrame!);
        var id = reqDoc.RootElement.GetProperty("id").GetInt32();

        // Send a fake response back
        var response = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"capabilities\":{}}}";
        await serverWrite.WriteAsync(LspFrame.Encode(response));
        await serverWrite.FlushAsync();

        using var taskCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await pendingTask.WaitAsync(taskCts.Token);

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.True(result.TryGetProperty("capabilities", out _));

        serverWrite.Dispose();
        serverRead.Dispose();
    }

    [Fact]
    public async Task WaitForNotification_resolves_when_matching_message_arrives()
    {
        var (serverWrite, loopRead, serverRead, loopWrite) = MakePipes();

        await using var loop = new MessageLoop(loopRead, loopWrite);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waitTask = loop.WaitForNotificationAsync(
            msg => msg.method == "$/progress",
            cts.Token);

        var notification = """{"jsonrpc":"2.0","method":"$/progress","params":{"token":"WorkspaceReady","value":{"kind":"end","message":"done"}}}""";
        await serverWrite.WriteAsync(LspFrame.Encode(notification));
        await serverWrite.FlushAsync();

        var received = await waitTask.WaitAsync(cts.Token);

        Assert.Equal("$/progress", received.method);

        serverWrite.Dispose();
        serverRead.Dispose();
    }

    [Fact]
    public async Task SendNotification_writes_frame_without_id()
    {
        var (serverWrite, loopRead, serverRead, loopWrite) = MakePipes();

        await using var loop = new MessageLoop(loopRead, loopWrite);

        await loop.SendNotificationAsync("initialized", new { });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var frame = await LspFrame.ReadNextAsync(serverRead, cts.Token);

        Assert.NotNull(frame);
        using var doc = JsonDocument.Parse(frame!);
        Assert.Equal("initialized", doc.RootElement.GetProperty("method").GetString());
        Assert.False(doc.RootElement.TryGetProperty("id", out _));

        serverWrite.Dispose();
        serverRead.Dispose();
    }
}
