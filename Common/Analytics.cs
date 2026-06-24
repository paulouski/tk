using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tk.Common;

public static class Analytics
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record AnalyticsEntry(
        string Ts,
        string Command,
        string? Sub,
        string[] Flags,
        int Operands,
        int Exit,
        string Detail,
        long DurationMs,
        long ShownChars,
        int ShownLines,
        long? RawChars,
        int? RawLines,
        string Ws);

    public static void Record(
        string[] commandArgs,
        int exitCode,
        string detail,
        long durationMs,
        long shownChars,
        int shownLines,
        long? rawChars,
        int? rawLines)
    {
        try
        {
            if (commandArgs.Length == 0)
                return;

            var optOut = Environment.GetEnvironmentVariable("TK_ANALYTICS");
            if (string.Equals(optOut, "0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(optOut, "false", StringComparison.OrdinalIgnoreCase))
                return;

            var (command, sub, flags, operands) = Classify(commandArgs);

            var ws = ComputeWorkspaceHash(Directory.GetCurrentDirectory());

            var entry = new AnalyticsEntry(
                Ts: DateTime.UtcNow.ToString("o"),
                Command: command,
                Sub: sub,
                Flags: flags,
                Operands: operands,
                Exit: exitCode,
                Detail: detail,
                DurationMs: durationMs,
                ShownChars: shownChars,
                ShownLines: shownLines,
                RawChars: rawChars,
                RawLines: rawLines,
                Ws: ws);

            var line = JsonSerializer.Serialize(entry, JsonOptions);

            var dir = TkPaths.AnalyticsDir();
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, $"{DateTime.UtcNow:yyyy-MM}.jsonl");
            File.AppendAllText(path, line + "\n");
        }
        catch
        {
            // Analytics must never crash the caller.
        }
    }

    internal static (string command, string? sub, string[] flags, int operands) Classify(string[] commandArgs)
    {
        if (commandArgs.Length == 0)
            return (string.Empty, null, [], 0);

        var command = commandArgs[0];
        var wantsSub = command is "git" or "dotnet";

        string? sub = null;
        var flags = new List<string>();
        var operandCount = 0;

        for (var i = 1; i < commandArgs.Length; i++)
        {
            var arg = commandArgs[i];
            if (arg.StartsWith('-'))
            {
                flags.Add(arg);
            }
            else
            {
                // First non-flag arg after command for git/dotnet is the subcommand.
                if (wantsSub && sub is null)
                {
                    sub = arg;
                }
                else
                {
                    operandCount++;
                }
            }
        }

        return (command, sub, [.. flags], operandCount);
    }

    private static string ComputeWorkspaceHash(string workspaceRoot) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspaceRoot)))[..16]
            .ToLowerInvariant();
}
