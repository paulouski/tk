using System.Text;
using Tk.Lsp.Protocol;
using Xunit;

namespace Tk.Tests.Lsp;

public class LspFrameTests
{
    [Fact]
    public void Encode_produces_correct_content_length_header()
    {
        var json = """{"jsonrpc":"2.0","method":"test"}""";
        var bytes = LspFrame.Encode(json);
        var text = Encoding.UTF8.GetString(bytes);

        var bodyBytes = Encoding.UTF8.GetByteCount(json);
        Assert.StartsWith($"Content-Length: {bodyBytes}\r\n\r\n", text);
        Assert.EndsWith(json, text);
    }

    [Fact]
    public void TryReadNext_decodes_framed_message()
    {
        var json = """{"jsonrpc":"2.0","method":"initialized"}""";
        var framed = LspFrame.Encode(json);
        using var stream = new MemoryStream(framed);

        var result = LspFrame.TryReadNext(stream);

        Assert.Equal(json, result);
    }

    [Fact]
    public void TryReadNext_returns_null_on_empty_stream()
    {
        using var stream = new MemoryStream();

        var result = LspFrame.TryReadNext(stream);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadNextAsync_decodes_framed_message()
    {
        var json = """{"jsonrpc":"2.0","id":1,"result":{"capabilities":{}}}""";
        var framed = LspFrame.Encode(json);
        using var stream = new MemoryStream(framed);

        var result = await LspFrame.ReadNextAsync(stream, default);

        Assert.Equal(json, result);
    }

    [Fact]
    public async Task ReadNextAsync_returns_null_on_empty_stream()
    {
        using var stream = new MemoryStream();

        var result = await LspFrame.ReadNextAsync(stream, default);

        Assert.Null(result);
    }

    [Fact]
    public void Round_trip_encode_then_read_preserves_json()
    {
        var json = """{"jsonrpc":"2.0","id":42,"method":"textDocument/references","params":{"textDocument":{"uri":"file:///foo.cs"}}}""";
        var framed = LspFrame.Encode(json);
        using var stream = new MemoryStream(framed);

        var result = LspFrame.TryReadNext(stream);

        Assert.Equal(json, result);
    }

    [Fact]
    public void TryReadNext_can_read_multiple_sequential_frames()
    {
        var json1 = """{"jsonrpc":"2.0","method":"a"}""";
        var json2 = """{"jsonrpc":"2.0","method":"b"}""";
        var combined = LspFrame.Encode(json1).Concat(LspFrame.Encode(json2)).ToArray();
        using var stream = new MemoryStream(combined);

        var r1 = LspFrame.TryReadNext(stream);
        var r2 = LspFrame.TryReadNext(stream);
        var r3 = LspFrame.TryReadNext(stream);

        Assert.Equal(json1, r1);
        Assert.Equal(json2, r2);
        Assert.Null(r3);
    }

    [Fact]
    public void Encode_handles_unicode_content()
    {
        var json = """{"value":"héllo wörld"}""";
        var framed = LspFrame.Encode(json);
        using var stream = new MemoryStream(framed);

        var result = LspFrame.TryReadNext(stream);

        Assert.Equal(json, result);
    }
}
