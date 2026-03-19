using System.Text;

namespace Tk.Filters;

/// <summary>Compact git status: group by status, truncate untracked.</summary>
public sealed class GitStatusFilter : IOutputFilter
{
    private const int MaxUntracked = 20;
    private const int MaxPerSection = 25;

    public string Apply(string raw, int exitCode)
    {
        if (exitCode != 0) return raw;

        var lines = raw.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

        if (lines.Length == 0)
            return $"{Ansi.Green("ok")} git status: clean\n";

        var staged = new List<string>();
        var modified = new List<string>();
        var untracked = new List<string>();
        var other = new List<string>();
        string? branch = null;

        // Track which section we're in by watching header lines
        var section = Section.None;

        foreach (var line in lines)
        {
            if (line.StartsWith("On branch "))
            {
                branch = line["On branch ".Length..].Trim();
                continue;
            }

            if (line.StartsWith("Changes to be committed:"))
            {
                section = Section.Staged;
                continue;
            }
            if (line.StartsWith("Changes not staged"))
            {
                section = Section.Modified;
                continue;
            }
            if (line.StartsWith("Untracked files:"))
            {
                section = Section.Untracked;
                continue;
            }

            if (line.StartsWith("Your branch ") || line.StartsWith("  (use ") ||
                line.StartsWith("no changes added"))
                continue;

            if (line.StartsWith("nothing to commit"))
            {
                other.Add(line);
                continue;
            }

            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (!line.StartsWith("\t")) continue;

            switch (section)
            {
                case Section.Staged:
                    staged.Add(trimmed);
                    break;
                case Section.Modified:
                    modified.Add(trimmed);
                    break;
                case Section.Untracked:
                    untracked.Add(trimmed);
                    break;
            }
        }

        var sb = new StringBuilder();
        if (branch != null)
            sb.AppendLine($"[{branch}]");

        AppendSection(sb, Ansi.Green("Staged"), staged, MaxPerSection);
        AppendSection(sb, Ansi.Yellow("Modified"), modified, MaxPerSection);
        AppendSection(sb, Ansi.Dim("Untracked"), untracked, MaxUntracked);

        foreach (var o in other)
            sb.AppendLine(o);

        if (sb.Length == 0 || (branch != null && sb.ToString().Trim() == $"[{branch}]"))
            return $"{Ansi.Green("ok")} git status: clean ({branch})\n";

        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string title, List<string> items, int max)
    {
        if (items.Count == 0) return;
        sb.AppendLine($"{title} ({items.Count}):");
        foreach (var item in items.Take(max))
            sb.AppendLine($"  {item}");
        if (items.Count > max)
            sb.AppendLine(Ansi.Dim($"  ... +{items.Count - max} more"));
    }

    private enum Section { None, Staged, Modified, Untracked }
}
