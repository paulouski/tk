using System.Text;

namespace Tk.Lsp.Protocol;

/// <summary>
/// Content-Length framing for LSP JSON-RPC messages.
/// </summary>
public static class LspFrame
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Wraps JSON with the LSP Content-Length framing header.
    /// </summary>
    public static byte[] Encode(string json)
    {
        var body = Utf8NoBom.GetBytes(json);
        var header = Utf8NoBom.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var result = new byte[header.Length + body.Length];
        header.CopyTo(result, 0);
        body.CopyTo(result, header.Length);
        return result;
    }

    /// <summary>
    /// Reads one framed message synchronously. Returns null on EOF.
    /// </summary>
    public static string? TryReadNext(Stream stream)
    {
        var contentLength = ReadContentLength(stream);
        if (contentLength < 0)
            return null;

        var buffer = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = stream.Read(buffer, read, contentLength - read);
            if (n == 0)
                return null;
            read += n;
        }

        return Utf8NoBom.GetString(buffer);
    }

    /// <summary>
    /// Reads one framed message asynchronously. Returns null on EOF.
    /// </summary>
    public static async Task<string?> ReadNextAsync(Stream stream, CancellationToken ct)
    {
        var contentLength = await ReadContentLengthAsync(stream, ct).ConfigureAwait(false);
        if (contentLength < 0)
            return null;

        var buffer = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, contentLength - read), ct).ConfigureAwait(false);
            if (n == 0)
                return null;
            read += n;
        }

        return Utf8NoBom.GetString(buffer);
    }

    // Reads headers until \r\n\r\n and extracts Content-Length. Returns -1 on EOF.
    private static int ReadContentLength(Stream stream)
    {
        var headerBytes = ReadUntilDoubleNewline(stream);
        if (headerBytes is null)
            return -1;
        return ParseContentLength(headerBytes);
    }

    private static async Task<int> ReadContentLengthAsync(Stream stream, CancellationToken ct)
    {
        var headerBytes = await ReadUntilDoubleNewlineAsync(stream, ct).ConfigureAwait(false);
        if (headerBytes is null)
            return -1;
        return ParseContentLength(headerBytes);
    }

    private static byte[]? ReadUntilDoubleNewline(Stream stream)
    {
        var buffer = new List<byte>(128);
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
                return buffer.Count == 0 ? null : null;
            buffer.Add((byte)b);
            if (buffer.Count >= 4 &&
                buffer[^4] == '\r' && buffer[^3] == '\n' &&
                buffer[^2] == '\r' && buffer[^1] == '\n')
            {
                return buffer.ToArray();
            }
        }
    }

    private static async Task<byte[]?> ReadUntilDoubleNewlineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(128);
        var singleByte = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(singleByte.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0)
                return buffer.Count == 0 ? null : null;
            buffer.Add(singleByte[0]);
            if (buffer.Count >= 4 &&
                buffer[^4] == '\r' && buffer[^3] == '\n' &&
                buffer[^2] == '\r' && buffer[^1] == '\n')
            {
                return buffer.ToArray();
            }
        }
    }

    private static int ParseContentLength(byte[] headerBytes)
    {
        var headerText = Encoding.ASCII.GetString(headerBytes);
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["Content-Length:".Length..].Trim();
                if (int.TryParse(value, out var len))
                    return len;
            }
        }
        return -1;
    }
}
