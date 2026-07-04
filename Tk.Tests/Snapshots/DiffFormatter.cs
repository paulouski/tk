using System.Text;

namespace Tk.Tests.Snapshots;

/// <summary>Small dependency-free unified-diff-style formatter for snapshot mismatch messages.
/// Not a general LCS diff (deliberately linear-time — one of our fixtures is a few thousand
/// lines) — it finds the common prefix/suffix and prints the differing middle block with a
/// couple of lines of context on each side.</summary>
public static class DiffFormatter
{
    private const int Context = 2;

    public static string Unified(string expected, string actual)
    {
        var expLines = expected.Split('\n');
        var actLines = actual.Split('\n');

        var minLen = Math.Min(expLines.Length, actLines.Length);
        var start = 0;
        while (start < minLen && expLines[start] == actLines[start])
            start++;

        var expEnd = expLines.Length - 1;
        var actEnd = actLines.Length - 1;
        while (expEnd >= start && actEnd >= start && expLines[expEnd] == actLines[actEnd])
        {
            expEnd--;
            actEnd--;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"--- expected ({expLines.Length} lines)");
        sb.AppendLine($"+++ actual ({actLines.Length} lines)");
        sb.AppendLine($"@@ first difference at line {start + 1} @@");

        for (var i = Math.Max(0, start - Context); i < start; i++)
            sb.AppendLine($"  {expLines[i]}");
        for (var i = start; i <= expEnd; i++)
            sb.AppendLine($"- {expLines[i]}");
        for (var i = start; i <= actEnd; i++)
            sb.AppendLine($"+ {actLines[i]}");

        var ctxEnd = Math.Min(expLines.Length - 1, expEnd + Context);
        for (var i = expEnd + 1; i <= ctxEnd; i++)
            sb.AppendLine($"  {expLines[i]}");

        return sb.ToString();
    }
}
