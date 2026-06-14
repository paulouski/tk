using System.Security.Cryptography;
using System.Text;

namespace Tk.Common;

public static class RawOutputStore
{
    public static string AppendFailureReference(string raw, string filtered, string[] commandArgs)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == filtered || filtered.Contains("raw=", StringComparison.Ordinal))
            return filtered;

        var path = Save(raw, commandArgs);
        return filtered.EndsWith('\n')
            ? $"{filtered}raw={path}\n"
            : $"{filtered}\nraw={path}\n";
    }

    private static string Save(string raw, string[] commandArgs)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tk-raw");
        Directory.CreateDirectory(dir);

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        var command = commandArgs.Length == 0 ? "cmd" : commandArgs[0];
        var safeCommand = new string(command.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', commandArgs)))).ToLowerInvariant()[..8];
        var path = Path.Combine(dir, $"{stamp}-{safeCommand}-{hash}.log");

        File.WriteAllText(path, raw);
        return path;
    }
}
