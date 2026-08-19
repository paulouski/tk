using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tk.Lsp;
using Tk.Lsp.Protocol;
using Xunit;

namespace Tk.Tests.Lsp;

[Collection("HomeSensitive")]
public class NamespaceFixerScopeNarrowingTests
{
    private static string NewWorkspaceRoot() => "/tmp/nsfixer-narrow-daemon-" + Guid.NewGuid();

    private static Socket BindFakeDaemon(string workspaceRoot)
    {
        var socketPath = DaemonSocket.GetSocketPath(workspaceRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        if (File.Exists(socketPath))
            File.Delete(socketPath);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        DaemonSocket.WritePidInfo(
            DaemonSocket.GetPidPath(workspaceRoot),
            new DaemonPidInfo(Environment.ProcessId, null));
        return listener;
    }

    private static void CleanupDaemonState(string workspaceRoot)
    {
        foreach (var path in new[]
                 {
                     DaemonSocket.GetSocketPath(workspaceRoot),
                     DaemonSocket.GetPidPath(workspaceRoot),
                     DaemonSocket.GetSocketPath(workspaceRoot) + ".lock"
                 })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static async Task RunFakeDaemonOnceAsync(Socket listener, object response)
    {
        using var conn = await listener.AcceptAsync();
        using var stream = new NetworkStream(conn, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

        await reader.ReadLineAsync();
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, LspMessage.Options));
    }

    [Fact]
    public void CollectDeclaredNames_flattens_nested_outline_tree()
    {
        var outline = new[]
        {
            new DocumentSymbolInfo(
                "Foo", "Class", 0, 0, 10, 0, null,
                Children:
                [
                    // Regression guard: the server's documentSymbol "name" for a method carries
                    // a signature suffix rather than the bare identifier — must be trimmed down
                    // to "Bar" so a plain call-site text match ("x.Bar()") still hits.
                    new DocumentSymbolInfo("Bar(Payment) : decimal", "Method", 1, 0, 2, 0, null, Children: null),
                    new DocumentSymbolInfo("Baz", "Property", 3, 0, 4, 0, null, Children: null),
                ]),
        };

        var names = NamespaceFixer.CollectDeclaredNames(outline);

        Assert.Equal(new HashSet<string> { "Foo", "Bar", "Baz" }, names);
    }

    [Fact]
    public void CollectDeclaredNames_skips_namespace_nodes_but_still_walks_their_children()
    {
        // Regression guard: a file-scoped namespace declaration is itself a DocumentSymbol node
        // (unmapped LSP SymbolKind.Namespace falls through to "symbol" in SymbolKindName). Its
        // own name ("Smoke" once trimmed from "Smoke.New") must be excluded — every other file
        // sharing the project's root namespace also contains "Smoke" as a whole word in its own
        // `namespace Smoke.*` declaration, so including it would defeat the narrowing entirely
        // by matching nearly every candidate file.
        var outline = new[]
        {
            new DocumentSymbolInfo(
                "Smoke.New", "symbol", 0, 0, 10, 0, null,
                Children:
                [
                    new DocumentSymbolInfo("PaymentExtensions", "class", 1, 0, 9, 0, null, Children: null),
                ]),
        };

        var names = NamespaceFixer.CollectDeclaredNames(outline);

        Assert.Equal(new HashSet<string> { "PaymentExtensions" }, names);
    }

    [Fact]
    public async Task FilterCandidatesByNamesAsync_keeps_files_with_whole_word_match()
    {
        var dir = Directory.CreateTempSubdirectory("nsfixer-narrow-test");
        try
        {
            var movedFile = Path.Combine(dir.FullName, "Moved.cs");
            var callerFile = Path.Combine(dir.FullName, "Caller.cs");
            var unrelatedFile = Path.Combine(dir.FullName, "Unrelated.cs");
            var lookalikeFile = Path.Combine(dir.FullName, "Lookalike.cs");

            await File.WriteAllTextAsync(movedFile, "namespace X;\npublic static class Moved { }\n");
            await File.WriteAllTextAsync(callerFile, "namespace Y;\nclass C { void M() { var x = payment.Bar(); } }\n");
            await File.WriteAllTextAsync(unrelatedFile, "namespace Z;\nclass D { void M() { } }\n");
            await File.WriteAllTextAsync(lookalikeFile, "namespace W;\nclass E { void M() { var x = new FooBar(); } }\n");

            var names = new HashSet<string> { "Foo", "Bar" };
            var candidates = new List<string> { movedFile, callerFile, unrelatedFile, lookalikeFile };

            var narrowed = await NamespaceFixer.FilterCandidatesByNamesAsync(
                candidates, movedFile, names, CancellationToken.None);

            Assert.Contains(movedFile, narrowed); // moved file always kept
            Assert.Contains(callerFile, narrowed); // contains "Bar" as a whole word
            Assert.DoesNotContain(unrelatedFile, narrowed); // contains neither name
            Assert.DoesNotContain(lookalikeFile, narrowed); // "FooBar" is not a whole-word match for "Foo" or "Bar"
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task FilterCandidatesByNamesAsync_matches_longer_name_even_when_a_shorter_name_is_its_prefix()
    {
        // Regression guard: when one declared name (e.g. "Foo") is a prefix of another
        // ("FooBar"), a file containing only "FooBar" must still be matched via the "FooBar"
        // alternative, not incorrectly dropped because "Foo" alone doesn't whole-word-match.
        var dir = Directory.CreateTempSubdirectory("nsfixer-narrow-prefix-test");
        try
        {
            var movedFile = Path.Combine(dir.FullName, "Moved.cs");
            var callerFile = Path.Combine(dir.FullName, "Caller.cs");

            await File.WriteAllTextAsync(movedFile, "namespace X;\npublic static class Moved { }\n");
            await File.WriteAllTextAsync(callerFile, "namespace Y;\nclass C { void M() { var x = new FooBar(); } }\n");

            var names = new HashSet<string> { "Foo", "FooBar" };
            var candidates = new List<string> { movedFile, callerFile };

            var narrowed = await NamespaceFixer.FilterCandidatesByNamesAsync(
                candidates, movedFile, names, CancellationToken.None);

            Assert.Contains(callerFile, narrowed);
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task NarrowScopeToReferencingFilesAsync_falls_back_to_full_list_when_outline_fails()
    {
        var root = NewWorkspaceRoot();
        using var listener = BindFakeDaemon(root);

        try
        {
            var serverTask = RunFakeDaemonOnceAsync(listener, new DaemonResponse(false, "outline failed", null));

            var movedFile = "/tmp/Moved.cs";
            var candidates = new List<string> { movedFile, "/tmp/Other.cs" };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await NamespaceFixer.NarrowScopeToReferencingFilesAsync(
                root, movedFile, candidates, cts.Token);

            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(candidates, result);
        }
        finally
        {
            listener.Close();
            CleanupDaemonState(root);
        }
    }
}
