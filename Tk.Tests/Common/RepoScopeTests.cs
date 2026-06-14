using Tk.Common;
using Xunit;

namespace Tk.Tests.Common;

public class RepoScopeTests
{
    // ShouldIncludeDirectory — unity mode

    [Theory]
    [InlineData("Library")]
    [InlineData("Temp")]
    [InlineData("Logs")]
    [InlineData("Build")]
    [InlineData("Builds")]
    [InlineData("UserSettings")]
    [InlineData("MemoryCaptures")]
    public void ShouldIncludeDirectory_unity_mode_hides_unity_ignored(string dirName)
    {
        var path = Path.Combine(Path.GetTempPath(), dirName);
        Assert.False(RepoScope.ShouldIncludeDirectory(path, includeIgnored: false, codeFocused: false, unityMode: true));
    }

    [Theory]
    [InlineData("Library")]
    [InlineData("Temp")]
    public void ShouldIncludeDirectory_default_mode_keeps_unity_ignored(string dirName)
    {
        var path = Path.Combine(Path.GetTempPath(), dirName);
        Assert.True(RepoScope.ShouldIncludeDirectory(path, includeIgnored: false, codeFocused: false, unityMode: false));
    }

    [Theory]
    [InlineData("Library")]
    [InlineData("Temp")]
    public void ShouldIncludeDirectory_unity_mode_with_include_ignored_keeps_unity_dirs(string dirName)
    {
        var path = Path.Combine(Path.GetTempPath(), dirName);
        // includeIgnored=true means --raw/--all, unity filter should also be skipped
        Assert.True(RepoScope.ShouldIncludeDirectory(path, includeIgnored: true, codeFocused: false, unityMode: true));
    }

    [Fact]
    public void ShouldIncludeDirectory_unity_mode_keeps_assets_in_code_focus()
    {
        var path = Path.Combine(Path.GetTempPath(), "Assets");
        Assert.True(RepoScope.ShouldIncludeDirectory(path, includeIgnored: false, codeFocused: true, unityMode: true));
    }

    [Fact]
    public void ShouldIncludeDirectory_default_mode_hides_assets_in_code_focus()
    {
        var path = Path.Combine(Path.GetTempPath(), "Assets");
        Assert.False(RepoScope.ShouldIncludeDirectory(path, includeIgnored: false, codeFocused: true, unityMode: false));
    }

    // ShouldIncludeFile — unity mode

    [Fact]
    public void ShouldIncludeFile_unity_mode_hides_meta_files()
    {
        Assert.False(RepoScope.ShouldIncludeFile("Assets/Foo.cs.meta", codeFocused: false, unityMode: true));
        Assert.False(RepoScope.ShouldIncludeFile("Assets/Texture.png.meta", codeFocused: false, unityMode: true));
    }

    [Fact]
    public void ShouldIncludeFile_default_mode_keeps_meta_files()
    {
        Assert.True(RepoScope.ShouldIncludeFile("Assets/Foo.cs.meta", codeFocused: false, unityMode: false));
    }

    [Fact]
    public void ShouldIncludeFile_unity_mode_keeps_non_meta_files()
    {
        Assert.True(RepoScope.ShouldIncludeFile("Assets/Foo.cs", codeFocused: false, unityMode: true));
    }

    // IsCodeFile — unity mode

    [Theory]
    [InlineData(".shader")]
    [InlineData(".hlsl")]
    [InlineData(".cginc")]
    [InlineData(".compute")]
    [InlineData(".asmdef")]
    [InlineData(".asmref")]
    [InlineData(".uxml")]
    [InlineData(".uss")]
    [InlineData(".inputactions")]
    public void IsCodeFile_unity_mode_treats_unity_extensions_as_code(string ext)
    {
        Assert.True(RepoScope.IsCodeFile($"Assets/Foo{ext}", unityMode: true));
    }

    [Theory]
    [InlineData(".shader")]
    [InlineData(".hlsl")]
    [InlineData(".compute")]
    public void IsCodeFile_default_mode_does_not_treat_unity_extensions_as_code(string ext)
    {
        Assert.False(RepoScope.IsCodeFile($"Assets/Foo{ext}", unityMode: false));
    }

    [Fact]
    public void IsCodeFile_unity_mode_still_returns_true_for_standard_code_files()
    {
        Assert.True(RepoScope.IsCodeFile("Assets/Foo.cs", unityMode: true));
    }

    [Fact]
    public void IsUnityProject_returns_true_when_project_version_file_exists()
    {
        var temp = Directory.CreateTempSubdirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(temp.FullName, "ProjectSettings"));
            File.WriteAllText(Path.Combine(temp.FullName, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.0f1");
            Assert.True(RepoScope.IsUnityProject(temp.FullName));
        }
        finally
        {
            Directory.Delete(temp.FullName, recursive: true);
        }
    }

    [Fact]
    public void IsUnityProject_returns_false_when_no_project_version_file()
    {
        var temp = Directory.CreateTempSubdirectory();
        try
        {
            Assert.False(RepoScope.IsUnityProject(temp.FullName));
        }
        finally
        {
            Directory.Delete(temp.FullName, recursive: true);
        }
    }
}
