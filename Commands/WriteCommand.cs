using System.Security.Cryptography;

namespace Tk.Commands;

/// <summary>
/// Replaces a line range in a file with stdin content, given explicit 1-based line numbers
/// instead of an old/new string diff. Lets an agent make a targeted edit without re-pasting
/// the surrounding text.
///
/// <para><c>&lt;range&gt;</c> is <c>start</c> (single line) or <c>start-end</c> (inclusive).
/// <c>end == start-1</c> means insertion before line <c>start</c> (nothing removed); any other
/// <c>end &lt; start</c> is an error. Appending at EOF is <c>(n+1)-(n)</c>.</para>
///
/// <para>A stale-line-numbers guard is mandatory: at least one of
/// <c>--expect-first</c>/<c>--expect-last</c>/<c>--expect-sha</c> must be given and must match
/// the file's CURRENT content, or the write is refused (fail-closed, non-zero exit, file
/// untouched).</para>
/// </summary>
public sealed class WriteCommand : ICommand
{
    public string Name => "write";

    private const string Usage = "usage: tk write <file> <start>[-<end>] [--expect-first <text>] [--expect-last <text>] [--expect-sha <hex>]";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        var (expectFirst, expectLast, expectSha, flagError) = ExtractAnchors(ctx.Args);
        if (flagError is not null)
        {
            ctx.Err.WriteLine($"tk write: {flagError}");
            return 1;
        }

        var positional = StripFlags(ctx.Args);
        if (positional.Length < 2)
        {
            ctx.Out.WriteLine(Usage);
            ctx.Out.WriteLine("       Replaces lines start..end with stdin content, verbatim.");
            ctx.Out.WriteLine("       end omitted -> single line. end == start-1 -> insert before start.");
            ctx.Out.WriteLine("       (n+1)-(n) appends at EOF. Requires at least one --expect-* anchor.");
            return 1;
        }

        var file = positional[0];
        var rangeArg = positional[1];

        if (!TryParseRange(rangeArg, out var start, out var end, out var rangeError))
        {
            ctx.Err.WriteLine($"tk write: {rangeError}");
            return 1;
        }

        if (expectFirst is null && expectLast is null && expectSha is null)
        {
            ctx.Err.WriteLine(
                "tk write requires an anchor (--expect-first / --expect-last / --expect-sha) to guard against stale line numbers");
            return 1;
        }

        if (!File.Exists(file))
        {
            ctx.Err.WriteLine($"tk write: file not found: {file}");
            return 1;
        }

        var originalBytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
        var text = System.Text.Encoding.UTF8.GetString(originalBytes);
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var (lines, hadTrailingNewline) = SplitLines(text);
        var nLines = lines.Count;

        var isInsertion = end == start - 1;
        if (!isInsertion && end < start)
        {
            ctx.Err.WriteLine($"tk write: invalid range {rangeArg}: end before start");
            return 1;
        }

        if (start < 1 || (isInsertion ? start > nLines + 1 : end > nLines))
        {
            ctx.Err.WriteLine($"tk write: range {rangeArg} out of bounds (file has {nLines} lines)");
            return 1;
        }

        // Anchor validation — must run against the CURRENT file, before any write.
        if (expectSha is not null)
        {
            var actualSha = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
            var expectedSha = expectSha.ToLowerInvariant();
            if (expectedSha.Length < 7 || !actualSha.StartsWith(expectedSha, StringComparison.Ordinal))
            {
                ctx.Err.WriteLine(
                    $"tk write aborted: sha anchor mismatch — file sha256 is \"{actualSha}\", expected to start with \"{expectedSha}\". " +
                    "Re-read the file and retry with a fresh --expect-sha.");
                return 1;
            }
        }

        var isAppend = isInsertion && start == nLines + 1;

        if (expectFirst is not null)
        {
            if (isAppend)
            {
                ctx.Err.WriteLine("tk write: --expect-first does not apply to an append at EOF; use --expect-sha instead");
                return 1;
            }
            var actual = lines[start - 1];
            if (!actual.Trim().StartsWith(expectFirst.Trim(), StringComparison.Ordinal))
            {
                ctx.Err.WriteLine(FormatAnchorMismatch("first", start, actual, expectFirst));
                return 1;
            }
        }

        if (expectLast is not null)
        {
            if (isInsertion)
            {
                ctx.Err.WriteLine("tk write: --expect-last does not apply to an insertion range");
                return 1;
            }
            var actual = lines[end - 1];
            if (!actual.Trim().StartsWith(expectLast.Trim(), StringComparison.Ordinal))
            {
                ctx.Err.WriteLine(FormatAnchorMismatch("last", end, actual, expectLast));
                return 1;
            }
        }

        var stdin = await Console.In.ReadToEndAsync().ConfigureAwait(false);
        var (contentLines, _) = SplitLines(stdin);

        var startIdx = start - 1;
        var removedCount = end - start + 1; // 0 for insertion
        var newLines = new List<string>(nLines - removedCount + contentLines.Count);
        newLines.AddRange(lines.Take(startIdx));
        newLines.AddRange(contentLines);
        newLines.AddRange(lines.Skip(startIdx + removedCount));

        var newText = string.Join(newline, newLines);
        if (hadTrailingNewline && newLines.Count > 0)
            newText += newline;

        await AtomicWriteAsync(file, newText).ConfigureAwait(false);

        ctx.Out.WriteLine($"ok write {file} {start}-{end} (-{removedCount} +{contentLines.Count})");
        return 0;
    }

    private static string FormatAnchorMismatch(string which, int lineNo, string actual, string expected)
    {
        const int maxLen = 80;
        var truncated = actual.Length > maxLen ? actual[..maxLen] + "..." : actual;
        return $"tk write aborted: {which} anchor mismatch — line {lineNo} is \"{truncated}\", " +
               $"expected to start with \"{expected}\". Re-read the slice (offset/limit) and retry with fresh line numbers.";
    }

    private static async Task AtomicWriteAsync(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var tempPath = Path.Combine(dir, $".{Path.GetFileName(path)}.tk-write-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tempPath, content, new System.Text.UTF8Encoding(false)).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>Splits text into lines (no terminators) plus whether it ended with a newline.
    /// Treats "\r\n" and "\n" uniformly; an empty string has zero lines.</summary>
    internal static (List<string> Lines, bool HadTrailingNewline) SplitLines(string text)
    {
        if (text.Length == 0)
            return ([], false);

        var raw = text.Split('\n');
        var hadTrailingNewline = raw[^1].Length == 0;
        var count = hadTrailingNewline ? raw.Length - 1 : raw.Length;
        var lines = new List<string>(count);
        for (var i = 0; i < count; i++)
            lines.Add(raw[i].EndsWith('\r') ? raw[i][..^1] : raw[i]);
        return (lines, hadTrailingNewline);
    }

    internal static bool TryParseRange(string rangeArg, out int start, out int end, out string? error)
    {
        start = 0;
        end = 0;
        error = null;

        var dashIdx = rangeArg.IndexOf('-');
        string startText;
        string? endText = null;
        if (dashIdx < 0)
        {
            startText = rangeArg;
        }
        else
        {
            startText = rangeArg[..dashIdx];
            endText = rangeArg[(dashIdx + 1)..];
        }

        if (!int.TryParse(startText, out start) || start < 1)
        {
            error = $"invalid range: {rangeArg}";
            return false;
        }

        if (endText is null)
        {
            end = start;
            return true;
        }

        if (!int.TryParse(endText, out end))
        {
            error = $"invalid range: {rangeArg}";
            return false;
        }

        return true;
    }

    private static (string? ExpectFirst, string? ExpectLast, string? ExpectSha, string? Error) ExtractAnchors(string[] args)
    {
        string? first = null, last = null, sha = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--expect-first":
                    if (i + 1 >= args.Length) return (null, null, null, "--expect-first requires a value");
                    first = args[++i];
                    break;
                case "--expect-last":
                    if (i + 1 >= args.Length) return (null, null, null, "--expect-last requires a value");
                    last = args[++i];
                    break;
                case "--expect-sha":
                    if (i + 1 >= args.Length) return (null, null, null, "--expect-sha requires a value");
                    sha = args[++i];
                    break;
            }
        }
        return (first, last, sha, null);
    }

    private static string[] StripFlags(string[] args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--expect-first" or "--expect-last" or "--expect-sha") { i++; continue; }
            result.Add(args[i]);
        }
        return [.. result];
    }
}
