using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

/// <summary>
/// Unit tests for <see cref="DiagCommand.ParseChangedCsFiles"/>, the pure porcelain-to-file-list
/// helper backing `tk diag --changed`. Uses absolute paths in the fabricated porcelain lines
/// (rather than paths relative to a fake repo root) so the test is independent of the process's
/// current directory — Path.GetFullPath is a no-op on an already-absolute path.
/// </summary>
public class DiagCommandChangedFilesTests
{
    [Fact]
    public void Empty_porcelain_yields_no_files()
    {
        var files = DiagCommand.ParseChangedCsFiles("");
        Assert.Empty(files);
    }

    [Fact]
    public void Staged_modified_and_untracked_cs_files_are_all_included()
    {
        var root = Directory.CreateTempSubdirectory("DiagChangedTest_").FullName;
        try
        {
            var staged = Path.Combine(root, "Staged.cs");
            var modified = Path.Combine(root, "Modified.cs");
            var untracked = Path.Combine(root, "Untracked.cs");
            File.WriteAllText(staged, "class Staged {}");
            File.WriteAllText(modified, "class Modified {}");
            File.WriteAllText(untracked, "class Untracked {}");

            var porcelain = $"M  {staged}\n M {modified}\n?? {untracked}\n";
            var files = DiagCommand.ParseChangedCsFiles(porcelain);

            Assert.Contains(staged, files);
            Assert.Contains(modified, files);
            Assert.Contains(untracked, files);
            Assert.Equal(3, files.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Non_cs_files_are_filtered_out()
    {
        var root = Directory.CreateTempSubdirectory("DiagChangedTest_").FullName;
        try
        {
            var csFile = Path.Combine(root, "A.cs");
            var mdFile = Path.Combine(root, "readme.md");
            File.WriteAllText(csFile, "class A {}");
            File.WriteAllText(mdFile, "docs");

            var porcelain = $"?? {csFile}\n?? {mdFile}\n";
            var files = DiagCommand.ParseChangedCsFiles(porcelain);

            Assert.Equal([csFile], files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deleted_files_are_skipped_since_there_is_nothing_left_to_diagnose()
    {
        var root = Directory.CreateTempSubdirectory("DiagChangedTest_").FullName;
        try
        {
            var deletedPath = Path.Combine(root, "Gone.cs");
            var porcelain = $" D {deletedPath}\n";
            var files = DiagCommand.ParseChangedCsFiles(porcelain);

            Assert.Empty(files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Renamed_file_keeps_only_the_new_path()
    {
        var root = Directory.CreateTempSubdirectory("DiagChangedTest_").FullName;
        try
        {
            var oldPath = Path.Combine(root, "Old.cs");
            var newPath = Path.Combine(root, "New.cs");
            File.WriteAllText(newPath, "class New {}");

            var porcelain = $"R  {oldPath} -> {newPath}\n";
            var files = DiagCommand.ParseChangedCsFiles(porcelain);

            Assert.Equal([newPath], files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Duplicate_paths_are_deduplicated()
    {
        var root = Directory.CreateTempSubdirectory("DiagChangedTest_").FullName;
        try
        {
            var path = Path.Combine(root, "A.cs");
            File.WriteAllText(path, "class A {}");

            var porcelain = $"M  {path}\nM  {path}\n";
            var files = DiagCommand.ParseChangedCsFiles(porcelain);

            Assert.Equal([path], files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
