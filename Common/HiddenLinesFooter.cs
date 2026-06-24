using Tk;

namespace Tk.Common;

public static class HiddenLinesFooter
{
    /// <summary>
    /// Returns a compact footer line when lines were hidden by filtering, or null when no footer is needed.
    /// </summary>
    public static string? Format(int originalLines, int shownLines, DetailLevel level)
    {
        if (originalLines <= 0)
            return null;
        if (shownLines >= originalLines)
            return null;

        var hidden = originalLines - shownLines;
        return level == DetailLevel.More
            ? $"hid={hidden}/{originalLines} (--raw)"
            : $"hid={hidden}/{originalLines} (--more, --raw)";
    }

    public static int CountLines(string text)
    {
        var lines = text.Split('\n');
        return lines.Length > 0 && lines[^1] == string.Empty
            ? lines.Length - 1
            : lines.Length;
    }
}
