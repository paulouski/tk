using System.Text.Json;
using Tk.Lsp.Protocol;

namespace Tk.Lsp;

/// <summary>
/// Language backend for C# using the Roslyn language server.
/// </summary>
public sealed class CSharpBackend : ILanguageBackend
{
    public string Name => "csharp";
    public string[] FileExtensions => [".cs"];
    public string[] WorkspaceMarkers => [".sln", ".csproj"];
    public string InstallHint => "Install C# Dev Kit in VS Code or run: dotnet tool install -g csharp-ls";

    public string? ResolveServer()
    {
        // (a) environment variable override
        var envPath = Environment.GetEnvironmentVariable("TK_LSP_CSHARP_SERVER");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return envPath;

        // (b) VS Code extension glob
        var vscodeDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode-insiders", "extensions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode-server", "extensions"),
        };

        foreach (var extDir in vscodeDirs)
        {
            if (!Directory.Exists(extDir))
                continue;

            var candidates = Directory.EnumerateDirectories(extDir, "ms-dotnettools.csharp-*")
                .OrderDescending(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var candidate in candidates)
            {
                var serverPath = Path.Combine(candidate, ".roslyn", "Microsoft.CodeAnalysis.LanguageServer");
                if (File.Exists(serverPath))
                    return serverPath;
            }
        }

        // (c) PATH: csharp-ls
        var whichResult = TryWhich("csharp-ls");
        if (whichResult is not null)
            return whichResult;

        return null;
    }

    public string[] GetLaunchArgs(string serverPath) =>
        [serverPath, "--stdio", "--autoLoadProjects", "--logLevel", "Warning"];

    public bool IsReadySignal(LspIncoming msg)
    {
        if (msg.method != "$/progress")
            return false;

        if (msg.@params is not { } prms)
            return false;

        try
        {
            if (!prms.TryGetProperty("token", out var tokenEl))
                return false;
            if (tokenEl.GetString() != "WorkspaceReady")
                return false;

            if (!prms.TryGetProperty("value", out var valueEl))
                return false;

            if (!valueEl.TryGetProperty("kind", out var kindEl))
                return false;

            return kindEl.GetString() == "end";
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string? TryWhich(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var isWindows = OperatingSystem.IsWindows();
        string[] extensions = isWindows ? [".exe", ".cmd", ""] : [""];
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var full = Path.Combine(dir, name + ext);
                if (File.Exists(full))
                    return full;
            }
        }
        return null;
    }
}
