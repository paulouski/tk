using System.Text;

namespace Tk.Common;

/// <summary>
/// Wraps an inner <see cref="TextWriter"/>, forwards all writes to it, and counts characters
/// and newlines written so the caller can measure shown output size without touching each command.
/// </summary>
public sealed class CountingTextWriter : TextWriter
{
    private readonly TextWriter _inner;

    public CountingTextWriter(TextWriter inner)
    {
        _inner = inner;
    }

    public override Encoding Encoding => _inner.Encoding;

    public long CharCount { get; private set; }

    /// <summary>Number of newline characters ('\n') seen across all writes.</summary>
    public int Lines { get; private set; }

    public override void Write(char value)
    {
        _inner.Write(value);
        CharCount++;
        if (value == '\n')
            Lines++;
    }

    public override void Write(string? value)
    {
        if (value is null)
            return;
        _inner.Write(value);
        CharCount += value.Length;
        foreach (var c in value)
            if (c == '\n')
                Lines++;
    }

    public override void Write(char[] buffer, int index, int count)
    {
        _inner.Write(buffer, index, count);
        CharCount += count;
        for (var i = index; i < index + count; i++)
            if (buffer[i] == '\n')
                Lines++;
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync() => _inner.FlushAsync();
}
