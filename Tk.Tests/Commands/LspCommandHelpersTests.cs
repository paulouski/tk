using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

/// <summary>
/// Unit tests for <see cref="LspCommandHelpers.ResolveWorkspaceRoot"/> — specifically that it
/// resolves from a given start path rather than always falling back to the current directory
/// (see DotnetWorkspaceResolverTests for the underlying walk-up logic).
/// </summary>
public class LspCommandHelpersTests
{
    [Fact]
    public void ResolveWorkspaceRoot_with_startPath_resolves_from_nested_file_ignoring_cwd()
    {
        var root = Directory.CreateTempSubdirectory("LspCommandHelpersTest_").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project />");
            var sub = Directory.CreateDirectory(Path.Combine(root, "src", "Nested")).FullName;
            var nestedFile = Path.Combine(sub, "Foo.cs");
            File.WriteAllText(nestedFile, "class Foo {}");

            // cwd (the test runner's directory) has nothing to do with this synthetic tree —
            // only the explicit startPath argument should drive resolution.
            var resolved = LspCommandHelpers.ResolveWorkspaceRoot(nestedFile);

            Assert.Equal(Path.GetFullPath(root), resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
