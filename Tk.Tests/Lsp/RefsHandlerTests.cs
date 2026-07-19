using System.IO.Pipes;
using System.Text.Json;
using Tk.Lsp;
using Tk.Lsp.Protocol;
using Tk.Lsp.RequestHandlers;
using Xunit;

namespace Tk.Tests.Lsp;

public class RefsHandlerTests
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

    private static LspDaemonContext MakeContext(MessageLoop loop) => new(
        loop,
        WaitForReadyAsync: _ => Task.CompletedTask,
        EnsureFileOpenAsync: (_, _, _) => Task.CompletedTask,
        Log: _ => { });

    [Fact]
    public async Task Position_based_refs_request_sets_includeDeclaration_false()
    {
        var (serverWrite, loopRead, serverRead, loopWrite) = MakePipes();
        await using var loop = new MessageLoop(loopRead, loopWrite);
        var ctx = MakeContext(loop);
        var handler = new RefsHandler();

        var request = new DaemonRequest("refs", "/proj/Foo.cs", 9, 4, null);
        var handleTask = handler.HandleAsync(ctx, request, CancellationToken.None);

        using var outCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sentFrame = await LspFrame.ReadNextAsync(serverRead, outCts.Token);
        Assert.NotNull(sentFrame);
        using var reqDoc = JsonDocument.Parse(sentFrame!);
        Assert.Equal("textDocument/references", reqDoc.RootElement.GetProperty("method").GetString());
        var context = reqDoc.RootElement.GetProperty("params").GetProperty("context");
        Assert.False(context.GetProperty("includeDeclaration").GetBoolean());

        var id = reqDoc.RootElement.GetProperty("id").GetInt32();
        var response = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":[]}";
        await serverWrite.WriteAsync(LspFrame.Encode(response));
        await serverWrite.FlushAsync();

        using var handleCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await handleTask.WaitAsync(handleCts.Token);
        Assert.True(result.Success);
        Assert.Empty(result.Locations!);

        serverWrite.Dispose();
        serverRead.Dispose();
    }

    [Fact]
    public async Task Symbol_based_refs_request_also_sets_includeDeclaration_false_and_returns_usages()
    {
        var (serverWrite, loopRead, serverRead, loopWrite) = MakePipes();
        await using var loop = new MessageLoop(loopRead, loopWrite);
        var ctx = MakeContext(loop);
        var handler = new RefsHandler();

        var request = new DaemonRequest("refs", null, 0, 0, "MyMethod");
        var handleTask = handler.HandleAsync(ctx, request, CancellationToken.None);

        // workspace/symbol lookup first (via SymbolResolver), resolving to a single match.
        using var symCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var symbolFrame = await LspFrame.ReadNextAsync(serverRead, symCts.Token);
        Assert.NotNull(symbolFrame);
        using var symDoc = JsonDocument.Parse(symbolFrame!);
        Assert.Equal("workspace/symbol", symDoc.RootElement.GetProperty("method").GetString());
        var symId = symDoc.RootElement.GetProperty("id").GetInt32();

        var symbolResponse = "{\"jsonrpc\":\"2.0\",\"id\":" + symId + ",\"result\":[" +
            "{\"name\":\"MyMethod\",\"kind\":6,\"location\":{\"uri\":\"file:///proj/Foo.cs\"," +
            "\"range\":{\"start\":{\"line\":9,\"character\":4},\"end\":{\"line\":9,\"character\":12}}}," +
            "\"containerName\":\"Foo\"}]}";
        await serverWrite.WriteAsync(LspFrame.Encode(symbolResponse));
        await serverWrite.FlushAsync();

        // Then the references request for the resolved position.
        using var refsCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var refsFrame = await LspFrame.ReadNextAsync(serverRead, refsCts.Token);
        Assert.NotNull(refsFrame);
        using var refsDoc = JsonDocument.Parse(refsFrame!);
        Assert.Equal("textDocument/references", refsDoc.RootElement.GetProperty("method").GetString());
        var context = refsDoc.RootElement.GetProperty("params").GetProperty("context");
        Assert.False(context.GetProperty("includeDeclaration").GetBoolean());

        var refsId = refsDoc.RootElement.GetProperty("id").GetInt32();
        var refsResponse = "{\"jsonrpc\":\"2.0\",\"id\":" + refsId + ",\"result\":[" +
            "{\"uri\":\"file:///proj/Caller.cs\",\"range\":{\"start\":{\"line\":3,\"character\":0},\"end\":{\"line\":3,\"character\":8}}}," +
            "{\"uri\":\"file:///proj/Other.cs\",\"range\":{\"start\":{\"line\":7,\"character\":2},\"end\":{\"line\":7,\"character\":10}}}]}";
        await serverWrite.WriteAsync(LspFrame.Encode(refsResponse));
        await serverWrite.FlushAsync();

        using var handleCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await handleTask.WaitAsync(handleCts.Token);
        Assert.True(result.Success);
        Assert.Equal(2, result.Locations!.Length);

        serverWrite.Dispose();
        serverRead.Dispose();
    }
}
