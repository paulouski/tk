namespace Tk.Commands.View;

/// <summary>
/// Line-by-line outline provider for YAML files, with a specialized path for
/// GitHub Actions workflows (<c>.github/workflows/*.yml</c> or any file whose
/// top level contains a <c>jobs:</c> key).
///
/// Workflow output surfaces the workflow name, the <c>on:</c> events, and a
/// per-job list with the job's line range and step count, formatted so an
/// agent can read the structure of a 1500-line cicd.yml without loading the
/// whole file. Generic YAML returns just the top-level keys with their
/// ranges.
///
/// Deliberately not a YAML parser: no anchor/alias resolution, no schema
/// validation, no block-folding awareness. Approximate ranges are marked
/// <c>source=yaml</c> in the output to signal they are structure-only.
/// </summary>
internal sealed class YamlOutlineProvider : IFileOutlineProvider
{
    public bool CanHandle(string path)
    {
        return path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);
    }

    public Task<OutlineResult?> GetOutlineAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch
        {
            // Unreadable file (permissions, transient I/O, race with delete). Fall
            // through to the regex provider — same contract as the other providers.
            return Task.FromResult<OutlineResult?>(null);
        }

        var entries = IsWorkflowFile(path, lines)
            ? BuildWorkflowEntries(lines)
            : BuildGenericEntries(lines);

        return Task.FromResult<OutlineResult?>(new OutlineResult("yaml", entries));
    }

    // ─── workflow detection ───────────────────────────────────────────────────

    private static bool IsWorkflowFile(string path, string[] lines)
    {
        // Path-based: the canonical signal. Cross-platform: normalize backslashes
        // so Windows-style absolute paths still match the `.github/workflows/`
        // segment the user typed in the spec.
        var normalized = path.Replace('\\', '/');
        if (normalized.Contains(".github/workflows/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Structural fallback: a top-level `jobs:` key is a strong workflow shape
        // signal even when the file isn't under .github/workflows/ (e.g. reusable
        // workflows, third-party CI that mirrors GHA's layout).
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] == ' ' || line[0] == '\t')
                continue;
            if (line.StartsWith("jobs:", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    // ─── workflow shape ───────────────────────────────────────────────────────

    private static List<OutlineEntry> BuildWorkflowEntries(string[] lines)
    {
        var entries = new List<OutlineEntry>();

        // `name:` is optional; emit only when present so the workflow line doesn't
        // appear as a blank "workflow: " on minimal files.
        var nameLine = FindTopLevelKey(lines, "name");
        if (nameLine >= 0)
        {
            var name = ExtractInlineValue(lines[nameLine]) ?? "";
            entries.Add(MakeEntry("meta", name, nameLine + 1, nameLine + 1, 1, null));
        }

        var onLine = FindTopLevelKey(lines, "on");
        if (onLine >= 0)
        {
            var events = ExtractOnEvents(lines, onLine);
            entries.Add(MakeEntry("on", events, onLine + 1, onLine + 1, 1, null));
        }

        var jobsIdx = FindTopLevelKey(lines, "jobs");
        if (jobsIdx >= 0)
        {
            entries.Add(MakeEntry("section", "jobs", jobsIdx + 1, jobsIdx + 1, 1, null));
            foreach (var job in ExtractJobs(lines, jobsIdx))
            {
                entries.Add(MakeEntry("job", job.Name, job.Start, job.End, job.BodySize, job.Steps.ToString()));
            }
        }

        return entries;
    }

    // ─── generic YAML shape ───────────────────────────────────────────────────

    private static List<OutlineEntry> BuildGenericEntries(string[] lines)
    {
        var keys = ExtractTopLevelKeys(lines);
        var entries = new List<OutlineEntry>(keys.Count);
        foreach (var (name, start, end) in keys)
        {
            entries.Add(MakeEntry("key", name, start, end, end - start + 1, null));
        }
        return entries;
    }

    // ─── entry helpers ────────────────────────────────────────────────────────

    private static OutlineEntry MakeEntry(string kind, string name, int start, int end, int bodySize, string? detail) =>
        new(kind, name, start, end, bodySize, detail, []);

    private static int FindTopLevelKey(string[] lines, string key)
    {
        // GHA workflows may quote `on` as `"on":` because YAML 1.1 treated `on` as
        // a boolean; accept both forms so quoted and unquoted workflow headers parse.
        var prefix = key + ":";
        var quoted = "\"" + key + "\":";
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || line[0] == ' ' || line[0] == '\t')
                continue;
            if (line.StartsWith(prefix, StringComparison.Ordinal) || line.StartsWith(quoted, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private static string? ExtractInlineValue(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0) return null;
        return line[(colon + 1)..].Trim();
    }

    private static string ExtractOnEvents(string[] lines, int onLineIdx)
    {
        var onLine = lines[onLineIdx];
        var after = ExtractInlineValue(onLine) ?? "";
        if (after.Length > 0)
            return NormalizeOnValue(after);

        // Block form (`on:` followed by indented event keys). Collect the first
        // token of each top-level key under the block until indent returns to 0.
        var events = new List<string>();
        for (var i = onLineIdx + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (CountLeadingSpaces(line) == 0)
                break;
            var trimmed = line.TrimStart();
            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = trimmed[..colon].Trim();
            if (key.Length > 0 && !key.Contains(' '))
                events.Add(key);
        }
        return string.Join(", ", events);
    }

    private static string NormalizeOnValue(string raw)
    {
        raw = raw.Trim();
        if (raw.Length >= 2 && raw[0] == '[' && raw[^1] == ']')
        {
            var inner = raw[1..^1];
            return string.Join(", ",
                inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        if (raw.Length >= 2 && raw[0] == '{' && raw[^1] == '}')
        {
            var inner = raw[1..^1];
            return string.Join(", ",
                inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => s.Split(':', 2)[0].Trim()));
        }
        return raw;
    }

    private readonly record struct JobOutline(string Name, int Start, int End, int BodySize, int Steps);

    private static List<JobOutline> ExtractJobs(string[] lines, int jobsIdx)
    {
        // Standard GHA layout: job keys live at exactly 2-space indent under
        // `jobs:`. Anything at indent 0 means we've left the jobs block.
        var jobStarts = new List<(string Name, int Line)>();
        for (var i = jobsIdx + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var indent = CountLeadingSpaces(line);
            if (indent == 0)
                break;
            if (indent != 2)
                continue;
            var trimmed = line.TrimStart();
            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = trimmed[..colon].Trim();
            if (name.Length == 0)
                continue;
            jobStarts.Add((name, i));
        }

        var jobs = new List<JobOutline>(jobStarts.Count);
        for (var i = 0; i < jobStarts.Count; i++)
        {
            var (name, start) = jobStarts[i];
            // End at the line just before the next job, or the last non-blank
            // line before the next top-level key, or end of file.
            var end = i + 1 < jobStarts.Count
                ? jobStarts[i + 1].Line - 1
                : FindJobSectionEnd(lines, start);
            var steps = CountSteps(lines, start, end);
            var bodySize = end - start + 1;
            jobs.Add(new JobOutline(name, start + 1, end + 1, bodySize, steps));
        }
        return jobs;
    }

    private static int FindJobSectionEnd(string[] lines, int start)
    {
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (CountLeadingSpaces(line) == 0)
                return i - 1;
        }
        return lines.Length - 1;
    }

    private static int CountSteps(string[] lines, int jobStart, int jobEnd)
    {
        // Locate `steps:` within the job. May be at any indent > 2.
        var stepsIdx = -1;
        for (var i = jobStart; i <= jobEnd; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var indent = CountLeadingSpaces(line);
            if (indent <= 2)
                continue;
            if (line.TrimStart().StartsWith("steps:", StringComparison.Ordinal))
            {
                stepsIdx = i;
                break;
            }
        }
        if (stepsIdx < 0)
            return 0;

        // Count `- ` list items under `steps:`. Anchor on the first item's indent
        // so sub-lists (e.g. `matrix:` entries) at deeper indents don't inflate
        // the count. Works for both 4-space (`    - `) and 6-space (`      - `)
        // step styles.
        var firstItemIndent = -1;
        var count = 0;
        for (var i = stepsIdx + 1; i <= jobEnd; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var indent = CountLeadingSpaces(line);
            if (indent <= 2)
                break;
            if (!line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                continue;
            if (firstItemIndent < 0)
            {
                firstItemIndent = indent;
                count++;
            }
            else if (indent == firstItemIndent)
            {
                count++;
            }
        }
        return count;
    }

    private static List<(string Name, int Start, int End)> ExtractTopLevelKeys(string[] lines)
    {
        var starts = new List<(string Name, int Line)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (line[0] == ' ' || line[0] == '\t')
                continue;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = line[..colon].Trim();
            if (name.Length == 0)
                continue;
            starts.Add((name, i));
        }

        var result = new List<(string, int, int)>(starts.Count);
        for (var i = 0; i < starts.Count; i++)
        {
            var (name, line) = starts[i];
            var end = i + 1 < starts.Count ? starts[i + 1].Line - 1 : lines.Length - 1;
            // Trim trailing blank lines so the range reflects actual content.
            while (end > line && string.IsNullOrWhiteSpace(lines[end]))
                end--;
            result.Add((name, line + 1, end + 1));
        }
        return result;
    }

    private static int CountLeadingSpaces(string line)
    {
        var count = 0;
        foreach (var c in line)
        {
            if (c == ' ') count++;
            else if (c == '\t') count += 4;
            else break;
        }
        return count;
    }
}
