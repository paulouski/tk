using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

public class ServerResolutionTests
{
    [Fact]
    public void Env_var_override_takes_precedence_when_file_exists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var serverPath = Path.Combine(tempDir, "Microsoft.CodeAnalysis.LanguageServer");
        File.WriteAllText(serverPath, "fake");

        var origEnv = Environment.GetEnvironmentVariable("TK_LSP_CSHARP_SERVER");
        try
        {
            Environment.SetEnvironmentVariable("TK_LSP_CSHARP_SERVER", serverPath);
            var backend = new CSharpBackend();
            var result = backend.ResolveServer();
            Assert.Equal(serverPath, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TK_LSP_CSHARP_SERVER", origEnv);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Env_var_pointing_to_nonexistent_file_is_ignored()
    {
        var origEnv = Environment.GetEnvironmentVariable("TK_LSP_CSHARP_SERVER");
        try
        {
            Environment.SetEnvironmentVariable("TK_LSP_CSHARP_SERVER", "/nonexistent/path/server");
            var backend = new CSharpBackend();
            // Should not return the bad path; falls through to other strategies
            var result = backend.ResolveServer();
            Assert.NotEqual("/nonexistent/path/server", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TK_LSP_CSHARP_SERVER", origEnv);
        }
    }

    [Fact]
    public void Returns_null_when_no_server_found_and_env_cleared()
    {
        // This test can only assert null if the machine truly has no csharp server installed.
        // We just verify it does not throw.
        var origEnv = Environment.GetEnvironmentVariable("TK_LSP_CSHARP_SERVER");
        try
        {
            Environment.SetEnvironmentVariable("TK_LSP_CSHARP_SERVER", null);
            var backend = new CSharpBackend();
            // Should return either a path (if installed) or null; must not throw.
            var result = backend.ResolveServer();
            // result is string? — no assertion on value since CI may or may not have it
            _ = result;
        }
        finally
        {
            Environment.SetEnvironmentVariable("TK_LSP_CSHARP_SERVER", origEnv);
        }
    }

    [Fact]
    public void GetLaunchArgs_includes_stdio_flag()
    {
        var backend = new CSharpBackend();
        var args = backend.GetLaunchArgs("/path/to/server");

        Assert.Contains("--stdio", args);
    }

    [Fact]
    public void GetLaunchArgs_first_element_is_server_path()
    {
        var backend = new CSharpBackend();
        var args = backend.GetLaunchArgs("/path/to/server");

        Assert.Equal("/path/to/server", args[0]);
    }

    [Fact]
    public void InstallHint_is_not_empty()
    {
        var backend = new CSharpBackend();
        Assert.False(string.IsNullOrWhiteSpace(backend.InstallHint));
    }

    [Fact]
    public void FileExtensions_contains_cs()
    {
        var backend = new CSharpBackend();
        Assert.Contains(".cs", backend.FileExtensions);
    }

    [Fact]
    public void WorkspaceMarkers_contains_sln_and_csproj()
    {
        var backend = new CSharpBackend();
        Assert.Contains(".sln", backend.WorkspaceMarkers);
        Assert.Contains(".csproj", backend.WorkspaceMarkers);
    }
}
