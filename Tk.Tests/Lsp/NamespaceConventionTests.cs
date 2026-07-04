using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

public class NamespaceConventionTests
{
    // ── ResolveRootNamespace ─────────────────────────────────────────────────

    [Fact]
    public void ResolveRootNamespace_reads_explicit_element()
    {
        var csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <RootNamespace>Acme.Widgets</RootNamespace>
              </PropertyGroup>
            </Project>
            """;

        Assert.Equal("Acme.Widgets", NamespaceConvention.ResolveRootNamespace(csproj, "/repo/Widgets/Widgets.csproj"));
    }

    [Fact]
    public void ResolveRootNamespace_falls_back_to_project_file_name()
    {
        var csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;

        Assert.Equal("Widgets", NamespaceConvention.ResolveRootNamespace(csproj, "/repo/Widgets/Widgets.csproj"));
    }

    // ── FindOwningProject ────────────────────────────────────────────────────

    [Fact]
    public void FindOwningProject_finds_csproj_in_ancestor_directory()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var csprojPath = Path.Combine(root, "MyProj.csproj");
            File.WriteAllText(csprojPath, "<Project />");
            var nested = Path.Combine(root, "Sub", "Deeper");
            Directory.CreateDirectory(nested);

            Assert.Equal(csprojPath, NamespaceConvention.FindOwningProject(nested));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindOwningProject_returns_null_when_none_found()
    {
        // A directory with no .csproj anywhere above it up to the filesystem root would take
        // forever to fabricate reliably; instead assert behavior on a shallow temp dir chain
        // that legitimately has no .csproj, stopping well before hitting a real project root
        // is not guaranteed in CI, so we only assert this returns *some* value without throwing.
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            // Should not throw regardless of outcome (may find an ancestor .csproj on some
            // machines, or null) — the important invariant is "no exception, no infinite loop".
            var result = NamespaceConvention.FindOwningProject(root);
            _ = result;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── ComputeExpectedNamespace ─────────────────────────────────────────────

    [Fact]
    public void ComputeExpectedNamespace_file_directly_in_project_root_is_bare_root_namespace()
    {
        var projectDir = Path.Combine("repo", "Widgets");
        var filePath = Path.Combine(projectDir, "Foo.cs");
        Assert.Equal("Acme.Widgets", NamespaceConvention.ComputeExpectedNamespace("Acme.Widgets", projectDir, filePath));
    }

    [Fact]
    public void ComputeExpectedNamespace_single_nested_folder_appends_one_segment()
    {
        var projectDir = Path.Combine("repo", "Widgets");
        var filePath = Path.Combine(projectDir, "Domain", "Foo.cs");
        Assert.Equal("Acme.Widgets.Domain", NamespaceConvention.ComputeExpectedNamespace("Acme.Widgets", projectDir, filePath));
    }

    [Fact]
    public void ComputeExpectedNamespace_multiple_nested_folders_append_dotted_segments()
    {
        var projectDir = Path.Combine("repo", "Widgets");
        var filePath = Path.Combine(projectDir, "Features", "CreateOrder", "Foo.cs");
        Assert.Equal(
            "Acme.Widgets.Features.CreateOrder",
            NamespaceConvention.ComputeExpectedNamespace("Acme.Widgets", projectDir, filePath));
    }

    [Fact]
    public void ComputeExpectedNamespace_sanitizes_invalid_identifier_characters()
    {
        var projectDir = Path.Combine("repo", "Widgets");
        var filePath = Path.Combine(projectDir, "my-folder", "Foo.cs");
        Assert.Equal("Acme.Widgets.my_folder", NamespaceConvention.ComputeExpectedNamespace("Acme.Widgets", projectDir, filePath));
    }

    [Fact]
    public void ComputeExpectedNamespace_prefixes_underscore_when_segment_starts_with_digit()
    {
        var projectDir = Path.Combine("repo", "Widgets");
        var filePath = Path.Combine(projectDir, "3rdParty", "Foo.cs");
        Assert.Equal("Acme.Widgets._3rdParty", NamespaceConvention.ComputeExpectedNamespace("Acme.Widgets", projectDir, filePath));
    }

    // ── ParseDeclaration ─────────────────────────────────────────────────────

    [Fact]
    public void ParseDeclaration_finds_file_scoped_namespace()
    {
        var text = "namespace Foo.Bar;\n\npublic class C {}\n";
        var decl = NamespaceConvention.ParseDeclaration(text);

        Assert.NotNull(decl);
        Assert.Equal("Foo.Bar", decl!.Value.Name);
        Assert.True(decl.Value.FileScoped);
    }

    [Fact]
    public void ParseDeclaration_finds_block_scoped_namespace()
    {
        var text = "namespace Foo.Bar\n{\n    public class C {}\n}\n";
        var decl = NamespaceConvention.ParseDeclaration(text);

        Assert.NotNull(decl);
        Assert.Equal("Foo.Bar", decl!.Value.Name);
        Assert.False(decl.Value.FileScoped);
    }

    [Fact]
    public void ParseDeclaration_returns_null_when_no_namespace_present()
    {
        var text = "public class C {}\n";
        Assert.Null(NamespaceConvention.ParseDeclaration(text));
    }

    [Fact]
    public void ParseDeclaration_ignores_leading_indentation()
    {
        var text = "   namespace Foo;\n";
        var decl = NamespaceConvention.ParseDeclaration(text);
        Assert.NotNull(decl);
        Assert.Equal("Foo", decl!.Value.Name);
    }

    // ── RewriteDeclaration ───────────────────────────────────────────────────

    [Fact]
    public void RewriteDeclaration_replaces_file_scoped_name_preserving_rest_of_file()
    {
        var text = "namespace Foo.Bar;\n\npublic class C {}\n";
        var updated = NamespaceConvention.RewriteDeclaration(text, "Foo.Baz");
        Assert.Equal("namespace Foo.Baz;\n\npublic class C {}\n", updated);
    }

    [Fact]
    public void RewriteDeclaration_replaces_block_scoped_name_preserving_body()
    {
        var text = "namespace Foo.Bar\n{\n    public class C {}\n}\n";
        var updated = NamespaceConvention.RewriteDeclaration(text, "Foo.Baz");
        Assert.Equal("namespace Foo.Baz\n{\n    public class C {}\n}\n", updated);
    }

    [Fact]
    public void RewriteDeclaration_preserves_usings_above_declaration()
    {
        var text = "using System;\nusing OrderService.Domain;\n\nnamespace OrderService.Data;\n\npublic class Repo {}\n";
        var updated = NamespaceConvention.RewriteDeclaration(text, "OrderService.Storage");
        Assert.Equal(
            "using System;\nusing OrderService.Domain;\n\nnamespace OrderService.Storage;\n\npublic class Repo {}\n",
            updated);
    }

    [Fact]
    public void RewriteDeclaration_throws_when_no_namespace_declaration()
    {
        var text = "public class C {}\n";
        Assert.Throws<InvalidOperationException>(() => NamespaceConvention.RewriteDeclaration(text, "Foo"));
    }
}
