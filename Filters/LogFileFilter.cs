using System.Text;
using System.Text.RegularExpressions;
using Tk;
using Tk.Common;

namespace Tk.Filters;

/// <summary>
/// Reads and filters ASP.NET service log files.
/// Strips noise (startup, MassTransit config, Npgsql debug, HttpClient pairs, IdentityModel).
/// Keeps: warn/fail/crit entries, exceptions (trimmed), HTTP errors, request URLs.
/// Usage: tk log &lt;file&gt; [--all] [--errors] [--last N]
/// </summary>
public static partial class LogFileFilter
{
    // A non-log file with more than this many non-empty lines but zero recognized log
    // entries is reported as a warning rather than a silent "ok n=0".
    private const int NonLogLineThreshold = 10;

    // Default tier caps the collapsed info summary to the N biggest normalized groups by
    // count, so high-cardinality business logs (unique-per-record info lines) don't flood
    // the default view. --more shows every group.
    private const int DefaultInfoSummaryLimit = 15;

    public static string Apply(string filePath, string[] flags, DetailLevel level = DetailLevel.Default) =>
        Apply(filePath, flags, level, out _);

    public static string Apply(string filePath, string[] flags, DetailLevel level, out int exitCode)
    {
        if (!File.Exists(filePath))
        {
            exitCode = 1;
            return $"File not found: {filePath}\n";
        }

        exitCode = 0;
        var raw = ReadAutoDetectEncoding(filePath);
        var lines = raw.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        var showAll = flags.Contains("--all");
        var errorsOnly = flags.Contains("--errors");
        var lastN = ExtractLastN(flags);

        if (showAll)
            return FormatPassthrough(lines, lastN);

        var rawEntries = ParseLogEntries(lines, out var unparsedCount);
        var nonEmptyLineCount = lines.Count(l => !string.IsNullOrWhiteSpace(l));
        if (rawEntries.Count == 0 && nonEmptyLineCount > NonLogLineThreshold)
        {
            exitCode = 1;
            return $"warn: no log entries recognized in {nonEmptyLineCount} lines — not an ASP.NET service log?\n";
        }

        var entries = rawEntries;
        if (errorsOnly)
            entries = entries.Where(e => e.Level is "fail" or "crit" or "error").ToList();
        else
            entries = FilterNoise(entries);

        entries = DeduplicateEntries(entries);

        // Default tier: warnings/errors in full, info collapsed to the top-N normalized
        // groups by count. --more (and --errors, which has no info anyway) keep every group.
        var infoGroupTotal = entries.Count(e => e.Level == "info");
        if (!errorsOnly && level != DetailLevel.More)
            entries = ApplyDefaultInfoTier(entries);

        if (lastN > 0)
            entries = entries.TakeLast(lastN).ToList();

        var result = FormatEntries(entries, filePath, unparsedCount, infoGroupTotal);
        var footer = HiddenLinesFooter.Format(lines.Length, HiddenLinesFooter.CountLines(result), level);
        if (footer is not null)
            result = result.EndsWith('\n') ? $"{result}{footer}\n" : $"{result}\n{footer}\n";
        return result;
    }

    /// <summary>Keeps the top <see cref="DefaultInfoSummaryLimit"/> info groups by repeat
    /// count (ties keep first-seen order); all non-info entries are always kept.</summary>
    private static List<LogEntry> ApplyDefaultInfoTier(List<LogEntry> entries)
    {
        var infoEntries = entries.Where(e => e.Level == "info").ToList();
        if (infoEntries.Count <= DefaultInfoSummaryLimit)
            return entries;

        var keepers = infoEntries
            .OrderByDescending(e => e.RepeatCount)
            .Take(DefaultInfoSummaryLimit)
            .ToHashSet();

        return entries.Where(e => e.Level != "info" || keepers.Contains(e)).ToList();
    }

    private static string ReadAutoDetectEncoding(string path)
    {
        var bytes = File.ReadAllBytes(path);

        // Detect UTF-16 LE BOM or null-byte pattern
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes);

        // Heuristic: if many null bytes, likely UTF-16
        if (bytes.Length > 100)
        {
            var nullCount = bytes.Take(200).Count(b => b == 0);
            if (nullCount > 50)
                return Encoding.Unicode.GetString(bytes);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static int ExtractLastN(string[] flags)
    {
        for (var i = 0; i < flags.Length - 1; i++)
        {
            if (flags[i] == "--last" && int.TryParse(flags[i + 1], out var n))
                return n;
        }
        return 0;
    }

    private static List<LogEntry> ParseLogEntries(string[] lines, out int unparsedCount)
    {
        var entries = new List<LogEntry>();
        LogEntry? current = null;
        unparsedCount = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var levelMatch = LogLevelRe().Match(line);
            if (levelMatch.Success)
            {
                current = new LogEntry
                {
                    Level = levelMatch.Groups["level"].Value.ToLowerInvariant(),
                    Source = levelMatch.Groups["source"].Value.Trim(),
                    Message = levelMatch.Groups["msg"].Value.Trim(),
                };
                entries.Add(current);
                continue;
            }

            if (!IsContinuationLike(line))
            {
                // No recognizable log prefix, not indented, no exception marker — this is
                // foreign/garbage content, not a genuine continuation. Count it rather than
                // silently gluing it to whatever entry came before.
                unparsedCount++;
                continue;
            }

            // Continuation line — first non-empty continuation becomes message if message is empty
            if (current != null)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(current.Message) && !string.IsNullOrEmpty(trimmed))
                    current.Message = trimmed;
                else
                    current.Continuation.Add(line);
            }
        }

        return entries;
    }

    /// <summary>True for lines that plausibly extend the previous entry: indented text
    /// (message bodies, stack frames) or an exception-type header line (which .NET often
    /// emits flush-left, e.g. "System.InvalidOperationException: boom").</summary>
    private static bool IsContinuationLike(string line) =>
        line.Length > 0 && (char.IsWhiteSpace(line[0]) ||
                             line.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                             line.TrimStart().StartsWith("---", StringComparison.Ordinal));

    private static List<LogEntry> FilterNoise(List<LogEntry> entries)
    {
        var result = new List<LogEntry>();
        var httpRequests = new Dictionary<string, HttpRequestInfo>();

        foreach (var entry in entries)
        {
            // Skip startup noise
            if (IsStartupNoise(entry)) continue;

            // Skip IdentityModel token validation spam
            if (entry.Source.Contains("IdentityLoggerAdapter")) continue;

            // Skip MassTransit debug
            if (entry.Level == "dbug" && entry.Source.Contains("MassTransit")) continue;

            // Skip Npgsql debug (SQL queries)
            if (entry.Level == "dbug" && entry.Source.Contains("Npgsql")) continue;

            // Skip hosting debug
            if (entry.Level == "dbug" && entry.Source.Contains("Hosting")) continue;

            // Collapse HttpClient 4-line pairs into one
            var fullText = $"{entry.Message} {string.Join(' ', entry.Continuation.Select(c => c.Trim()))}";
            if (entry.Source.Contains("HttpClient") || entry.Source.Contains("LogicalHandler") ||
                entry.Source.Contains("ClientHandler") ||
                fullText.Contains("HTTP request") || fullText.Contains("HTTP response"))
            {
                var collapsed = TryCollapseHttpRequest(entry, fullText, httpRequests);
                if (collapsed != null)
                    result.Add(collapsed);
                continue;
            }

            // Keep warn+ (fail/crit included — full stack preserved; the stack is the signal)
            if (entry.Level is "warn" or "fail" or "crit" or "error")
            {
                result.Add(entry);
                continue;
            }

            // Keep info only if it's something meaningful (not generic framework noise)
            if (entry.Level == "info" && !IsFrameworkNoise(entry))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private static bool IsStartupNoise(LogEntry entry)
    {
        var msg = entry.Message;
        var src = entry.Source;

        // MassTransit configuration
        if (msg.StartsWith("Configured endpoint") || msg.StartsWith("Starting bus") ||
            msg.StartsWith("Bus started") || msg.StartsWith("Topic:") ||
            msg.StartsWith("Subscription ") || msg.StartsWith("Endpoint Ready:"))
            return true;

        // .NET hosting lifecycle
        if (msg.StartsWith("Now listening on:") || msg.StartsWith("Application started") ||
            msg.StartsWith("Content root path:") || msg.StartsWith("Hosting environment:") ||
            msg.StartsWith("Hosting start") || msg.StartsWith("Host") && msg.Contains("start"))
            return true;

        // Build/search noise
        if (msg.StartsWith("Searching '") || msg.StartsWith("Building...") ||
            msg.StartsWith("Using launch settings"))
            return true;

        // Service-specific startup
        if (msg.StartsWith("Google Sheet schema") || msg.StartsWith("Skipping initial sync") ||
            msg.Contains("Worker is disabled") || msg.StartsWith("Opened physical connection") ||
            msg.StartsWith("Opened connection") || msg.StartsWith("Executing batch:"))
            return true;

        // Npgsql/Marten schema migration (source-based)
        if (src.Contains("Npgsql")) return true;

        return false;
    }

    private static bool IsFrameworkNoise(LogEntry entry)
    {
        var src = entry.Source;
        var msg = entry.Message;

        return src.Contains("IdentityLoggerAdapter") ||
               src.Contains("HttpClient.Refit") ||
               src.Contains("LogicalHandler") ||
               src.Contains("ClientHandler") ||
               src.Contains("Microsoft.Hosting.Lifetime") ||
               src.Contains("Npgsql") ||
               msg.StartsWith("IDX1") || // IdentityModel token validation codes
               msg.StartsWith("Executing batch:");
    }

    private static LogEntry? TryCollapseHttpRequest(LogEntry entry, string fullText,
        Dictionary<string, HttpRequestInfo> httpRequests)
    {
        var msg = fullText;

        // "Start processing HTTP request GET http://localhost:5002/api/v1/invoices/search?..."
        var startMatch = HttpStartRe().Match(msg);
        if (startMatch.Success)
        {
            var method = startMatch.Groups["method"].Value;
            var url = startMatch.Groups["url"].Value;
            var key = $"{method} {url}";
            httpRequests[key] = new HttpRequestInfo { Method = method, Url = ShortenUrl(url) };
            return null; // Wait for response
        }

        // "Sending HTTP request GET ..." — skip (duplicate of start)
        if (msg.StartsWith("Sending HTTP request")) return null;

        // "Received HTTP response headers after 97.8141ms - 200"
        var responseMatch = HttpResponseRe().Match(msg);
        if (responseMatch.Success)
        {
            var duration = responseMatch.Groups["duration"].Value;
            var status = responseMatch.Groups["status"].Value;

            // Find matching request (most recent)
            if (httpRequests.Count > 0)
            {
                var last = httpRequests.Last();
                httpRequests.Remove(last.Key);
                var info = last.Value;

                // Only show non-200 or slow requests (>1s)
                if (status != "200" || (double.TryParse(duration, out var ms) && ms > 1000))
                {
                    return new LogEntry
                    {
                        Level = status.StartsWith("2") ? "info" : (status.StartsWith("4") ? "warn" : "fail"),
                        Source = "HTTP",
                        Message = $"{info.Method} {info.Url} -> {status} ({duration}ms)"
                    };
                }
            }
            return null;
        }

        // "End processing HTTP request after ..." — skip (duplicate of received)
        if (msg.StartsWith("End processing")) return null;

        return null;
    }

    private static string ShortenUrl(string url)
    {
        // "http://localhost:5002/api/v1/invoices/search?query=uber&offset=0" -> "/api/v1/invoices/search?query=uber&offset=0"
        var idx = url.IndexOf("/api/", StringComparison.Ordinal);
        if (idx >= 0) return url[idx..];

        idx = url.IndexOf("://", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var pathStart = url.IndexOf('/', idx + 3);
            if (pathStart >= 0) return url[pathStart..];
        }
        return url;
    }

    private static List<LogEntry> DeduplicateEntries(List<LogEntry> entries)
    {
        var result = new List<LogEntry>();
        var seen = new Dictionary<string, int>(); // key -> index in result

        foreach (var entry in entries)
        {
            var key = BuildDedupKey(entry);

            if (seen.TryGetValue(key, out var existingIdx))
            {
                result[existingIdx].RepeatCount++;
            }
            else
            {
                seen[key] = result.Count;
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>
    /// Dedup key: level + source + first 100 chars of message. Warn/fail/crit/error keep the
    /// exact message text (precision matters more there, and volume is low). Info entries key
    /// on a normalized message so high-cardinality business logs (a different name/id/GUID per
    /// record) collapse into one group instead of flooding the output with near-duplicates.
    /// </summary>
    private static string BuildDedupKey(LogEntry entry)
    {
        var msg = entry.Level == "info" ? NormalizeForDedupKey(entry.Message) : entry.Message;
        var msgKey = msg.Length > 100 ? msg[..100] : msg;
        return $"{entry.Level}|{entry.Source}|{msgKey}";
    }

    /// <summary>
    /// Replaces obvious variable segments (GUIDs, ISO timestamps, quoted strings, long hex
    /// runs, plain numbers) with placeholders, purely for dedup keying. The displayed sample
    /// always uses the original, unmodified message — this never touches rendered output.
    /// Unicode/non-ASCII text (e.g. Polish diacritics) passes through untouched.
    /// </summary>
    internal static string NormalizeForDedupKey(string message)
    {
        if (string.IsNullOrEmpty(message))
            return message;

        var s = message;
        s = GuidRe().Replace(s, "<guid>");
        s = IsoTimestampRe().Replace(s, "<ts>");
        s = QuotedStringRe().Replace(s, "<str>");
        s = LongHexRe().Replace(s, "<hex>");
        s = NumberRe().Replace(s, "<num>");
        return s;
    }

    private static string FormatEntries(List<LogEntry> entries, string filePath, int unparsedCount = 0, int infoGroupTotal = 0)
    {
        if (entries.Count == 0)
        {
            var empty = $"ok log n=0 file={Path.GetFileName(filePath)}";
            if (unparsedCount > 0) empty += $" unparsed={unparsedCount}";
            return empty + "\n";
        }

        var sb = new StringBuilder();
        var fileName = Path.GetFileName(filePath);
        var errorCount = entries.Count(e => e.Level is "fail" or "crit" or "error");
        var warnCount = entries.Count(e => e.Level == "warn");
        var infoCount = entries.Count(e => e.Level == "info");

        sb.Append($"log n={entries.Count} file={fileName}");
        if (errorCount > 0) sb.Append($" e={errorCount}");
        if (warnCount > 0) sb.Append($" w={warnCount}");
        if (infoCount > 0)
        {
            sb.Append($" i={infoCount}");
            if (infoGroupTotal > infoCount) sb.Append($"/{infoGroupTotal} (--more)");
        }
        if (unparsedCount > 0) sb.Append($" unparsed={unparsedCount}");
        sb.AppendLine();
        sb.AppendLine(Ansi.Dim("---"));

        foreach (var entry in entries)
        {
            var levelTag = entry.Level switch
            {
                "fail" or "crit" or "error" => Ansi.Red("ERR"),
                "warn" => Ansi.Yellow("WRN"),
                _ => "INF"
            };

            var source = ShortenSource(entry.Source);
            var msg = entry.Message.Length > 200 ? entry.Message[..200] + "..." : entry.Message;
            var repeat = entry.RepeatCount > 1 ? $" (x{entry.RepeatCount})" : "";
            sb.AppendLine($"[{levelTag}] {source}: {msg}{repeat}");

            foreach (var cont in entry.Continuation)
            {
                var trimmed = cont.TrimEnd();
                if (trimmed.Length > 200) trimmed = trimmed[..200] + "...";
                sb.AppendLine($"  {trimmed}");
            }
        }

        return sb.ToString();
    }

    private static string FormatPassthrough(string[] lines, int lastN)
    {
        var filtered = lines.Where(l => !string.IsNullOrWhiteSpace(l));
        if (lastN > 0) filtered = filtered.TakeLast(lastN);
        return string.Join('\n', filtered) + "\n";
    }

    private static string ShortenSource(string source)
    {
        // "Fleet.Customer.Infrastructure.Consumers.ESign.ESignCompletedConsumer" -> "ESignCompletedConsumer"
        // "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware" -> "ExceptionHandlerMiddleware"
        var lastDot = source.LastIndexOf('.');
        return lastDot >= 0 ? source[(lastDot + 1)..] : source;
    }

    [GeneratedRegex(@"^(?<level>dbug|info|warn|fail|crit|error)\s*:\s*(?<source>[^\[]+?)(?:\[\d+\])?\s*$")]
    private static partial Regex LogLevelRe();

    [GeneratedRegex(@"Start processing HTTP request (?<method>GET|POST|PUT|DELETE|PATCH) (?<url>\S+)")]
    private static partial Regex HttpStartRe();

    [GeneratedRegex(@"Received HTTP response headers after (?<duration>[\d.]+)ms\s*-\s*(?<status>\d+)")]
    private static partial Regex HttpResponseRe();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidRe();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?\b")]
    private static partial Regex IsoTimestampRe();

    [GeneratedRegex("\"[^\"]*\"|'[^']*'")]
    private static partial Regex QuotedStringRe();

    // Hex run of 8+ chars, but only when it contains at least one a-f letter — otherwise it's
    // just a plain decimal number and NumberRe handles it.
    [GeneratedRegex(@"\b(?=[0-9a-fA-F]{8,}\b)(?=[0-9a-fA-F]*[a-fA-F])[0-9a-fA-F]{8,}\b")]
    private static partial Regex LongHexRe();

    [GeneratedRegex(@"\b\d+(?:\.\d+)?\b")]
    private static partial Regex NumberRe();

    private class LogEntry
    {
        public string Level { get; set; } = "";
        public string Source { get; set; } = "";
        public string Message { get; set; } = "";
        public List<string> Continuation { get; set; } = [];
        public int RepeatCount { get; set; } = 1;
    }

    private class HttpRequestInfo
    {
        public string Method { get; set; } = "";
        public string Url { get; set; } = "";
    }
}
