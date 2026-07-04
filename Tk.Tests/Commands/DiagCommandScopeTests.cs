using Tk.Commands;
using Xunit;

namespace Tk.Tests.Commands;

/// <summary>
/// Unit tests for <see cref="DiagCommand.ResolveScope"/>, the pure path-to-file-list helper
/// backing `tk diag`. No daemon or LSP server involved.
/// </summary>
public class DiagCommandScopeTests
{
    [Fact]
    public void Single_cs_file_resolves_to_itself()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"DiagScopeTest_{Guid.NewGuid():N}.cs");
        File.WriteAllText(tmp, "class C {}");
        try
        {
            var (files, total) = DiagCommand.ResolveScope(tmp, maxFiles: 300);

            Assert.Equal([tmp], files);
            Assert.Equal(1, total);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Directory_scope_finds_all_cs_files_recursively()
    {
        var root = Directory.CreateTempSubdirectory("DiagScopeTest_").FullName;
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(root, "Sub"));
            File.WriteAllText(Path.Combine(root, "A.cs"), "class A {}");
            File.WriteAllText(Path.Combine(sub.FullName, "B.cs"), "class B {}");
            File.WriteAllText(Path.Combine(root, "readme.md"), "not code");

            var (files, total) = DiagCommand.ResolveScope(root, maxFiles: 300);

            Assert.Equal(2, total);
            Assert.Equal(2, files.Count);
            Assert.Contains(files, f => f.EndsWith("A.cs", StringComparison.Ordinal));
            Assert.Contains(files, f => f.EndsWith("B.cs", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Csproj_path_resolves_to_its_directorys_cs_files()
    {
        var root = Directory.CreateTempSubdirectory("DiagScopeTest_").FullName;
        try
        {
            var csproj = Path.Combine(root, "Proj.csproj");
            File.WriteAllText(csproj, "<Project />");
            File.WriteAllText(Path.Combine(root, "A.cs"), "class A {}");

            var (files, total) = DiagCommand.ResolveScope(csproj, maxFiles: 300);

            Assert.Equal(1, total);
            Assert.Contains(files, f => f.EndsWith("A.cs", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Bin_and_obj_directories_are_excluded()
    {
        var root = Directory.CreateTempSubdirectory("DiagScopeTest_").FullName;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin"));
            var obj = Directory.CreateDirectory(Path.Combine(root, "obj"));
            File.WriteAllText(Path.Combine(root, "A.cs"), "class A {}");
            File.WriteAllText(Path.Combine(bin.FullName, "Generated.cs"), "class G {}");
            File.WriteAllText(Path.Combine(obj.FullName, "Generated2.cs"), "class G2 {}");

            var (files, total) = DiagCommand.ResolveScope(root, maxFiles: 300);

            Assert.Equal(1, total);
            Assert.Equal([Path.Combine(root, "A.cs")], files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scope_exceeding_cap_is_truncated_but_reports_true_total()
    {
        var root = Directory.CreateTempSubdirectory("DiagScopeTest_").FullName;
        try
        {
            for (var i = 0; i < 5; i++)
                File.WriteAllText(Path.Combine(root, $"F{i}.cs"), $"class F{i} {{}}");

            var (files, total) = DiagCommand.ResolveScope(root, maxFiles: 3);

            Assert.Equal(5, total);
            Assert.Equal(3, files.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
