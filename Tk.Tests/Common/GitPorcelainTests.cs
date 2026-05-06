using Tk.Common;
using Xunit;

namespace Tk.Tests.Common;

public class GitPorcelainTests
{
    [Theory]
    [InlineData(" M src/a.cs", ' ', 'M', "src/a.cs")]
    [InlineData("M  src/b.cs", 'M', ' ', "src/b.cs")]
    [InlineData("MM src/c.cs", 'M', 'M', "src/c.cs")]
    [InlineData("?? new.cs", '?', '?', "new.cs")]
    [InlineData("A  added.cs", 'A', ' ', "added.cs")]
    [InlineData("D  deleted.cs", 'D', ' ', "deleted.cs")]
    public void Parses_status_pair_and_path(string line, char x, char y, string path)
    {
        var entry = GitPorcelain.ParseLine(line);

        Assert.NotNull(entry);
        Assert.Equal(x, entry!.X);
        Assert.Equal(y, entry.Y);
        Assert.Equal(path, entry.Path);
    }

    [Fact]
    public void Rename_keeps_destination_path()
    {
        var entry = GitPorcelain.ParseLine("R  old.cs -> new/path.cs");

        Assert.NotNull(entry);
        Assert.Equal("new/path.cs", entry!.Path);
        Assert.Equal('R', entry.X);
    }

    [Fact]
    public void Quoted_path_is_unquoted()
    {
        var entry = GitPorcelain.ParseLine("?? \"file with spaces.cs\"");

        Assert.NotNull(entry);
        Assert.Equal("file with spaces.cs", entry!.Path);
    }

    [Fact]
    public void Rename_with_quoted_destination_returns_unquoted_path()
    {
        var entry = GitPorcelain.ParseLine("R  old.cs -> \"new file.cs\"");

        Assert.NotNull(entry);
        Assert.Equal("new file.cs", entry!.Path);
    }

    [Fact]
    public void Returns_null_for_lines_shorter_than_four_chars()
    {
        Assert.Null(GitPorcelain.ParseLine(""));
        Assert.Null(GitPorcelain.ParseLine("M"));
        Assert.Null(GitPorcelain.ParseLine("M  "));
    }

    [Fact]
    public void Returns_null_when_path_is_only_whitespace()
    {
        Assert.Null(GitPorcelain.ParseLine("M     "));
    }
}
