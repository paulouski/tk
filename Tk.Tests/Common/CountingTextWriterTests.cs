using System.Text;
using Tk.Common;
using Xunit;

namespace Tk.Tests.Common;

public class CountingTextWriterTests
{
    [Fact]
    public void Write_string_counts_chars_and_newlines()
    {
        var inner = new StringWriter();
        var writer = new CountingTextWriter(inner);

        writer.Write("hello\nworld\n");

        Assert.Equal(12, writer.CharCount);
        Assert.Equal(2, writer.Lines);
    }

    [Fact]
    public void Write_char_counts_individually()
    {
        var inner = new StringWriter();
        var writer = new CountingTextWriter(inner);

        writer.Write('a');
        writer.Write('\n');
        writer.Write('b');

        Assert.Equal(3, writer.CharCount);
        Assert.Equal(1, writer.Lines);
    }

    [Fact]
    public void Write_char_array_counts_correctly()
    {
        var inner = new StringWriter();
        var writer = new CountingTextWriter(inner);
        var buf = "foo\nbar\n".ToCharArray();

        writer.Write(buf, 0, buf.Length);

        Assert.Equal(8, writer.CharCount);
        Assert.Equal(2, writer.Lines);
    }

    [Fact]
    public void Inner_writer_receives_exact_same_text()
    {
        var inner = new StringWriter();
        var writer = new CountingTextWriter(inner);

        writer.Write("line one\n");
        writer.Write("line two\n");

        Assert.Equal("line one\nline two\n", inner.ToString());
    }

    [Fact]
    public void Write_null_string_is_noop()
    {
        var inner = new StringWriter();
        var writer = new CountingTextWriter(inner);

        writer.Write((string?)null);

        Assert.Equal(0, writer.CharCount);
        Assert.Equal(0, writer.Lines);
        Assert.Equal(string.Empty, inner.ToString());
    }

    [Fact]
    public void Multiple_writes_accumulate()
    {
        var inner = new StringWriter();
        var writer = new CountingTextWriter(inner);

        writer.Write("abc");
        writer.Write("def\n");

        Assert.Equal(7, writer.CharCount);
        Assert.Equal(1, writer.Lines);
    }

    [Fact]
    public void Encoding_matches_inner()
    {
        var inner = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        var writer = new CountingTextWriter(inner);

        Assert.Equal(inner.Encoding, writer.Encoding);
    }

    [Fact]
    public void Write_char_array_partial_slice()
    {
        var inner = new StringWriter();
        var writer = new CountingTextWriter(inner);
        var buf = "##hello\nworld##".ToCharArray();

        // Write only "hello\nworld" (indices 2..12, length 11)
        writer.Write(buf, 2, 11);

        Assert.Equal(11, writer.CharCount);
        Assert.Equal(1, writer.Lines);
        Assert.Equal("hello\nworld", inner.ToString());
    }
}
