using Tk.Common;
using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Common;

/// <summary>
/// Unit tests for <see cref="DotnetWorkspaceResolver.FindWarmestTarget"/> — the "prefer an
/// already-warm ancestor daemon over a colder, narrower sibling root" logic (see the umbrella
/// monorepo scenario in the type's doc comment).
/// </summary>
[Collection("HomeSensitive")]
public class DotnetWorkspaceResolverTests
{
    [Fact]
    public void Prefers_ancestor_sln_with_a_live_daemon_over_a_nearer_sibling_sln()
    {
        var ancestor = Directory.CreateTempSubdirectory("WarmestTargetTest_").FullName;
        try
        {
            var ancestorSln = Path.Combine(ancestor, "Umbrella.sln");
            File.WriteAllText(ancestorSln, "");

            var nested = Directory.CreateDirectory(Path.Combine(ancestor, "sub-repo")).FullName;
            var nestedSln = Path.Combine(nested, "SubRepo.sln");
            File.WriteAllText(nestedSln, "");
            var startFile = Path.Combine(nested, "Foo.cs");
            File.WriteAllText(startFile, "class Foo {}");

            // Fake a live daemon for the ancestor root only (self PID: guaranteed alive for the
            // duration of the test), none for the nested sibling root.
            var pidPath = DaemonSocket.GetPidPath(ancestor);
            DaemonSocket.WritePidInfo(pidPath, new DaemonPidInfo(Environment.ProcessId, null));
            try
            {
                var target = DotnetWorkspaceResolver.FindWarmestTarget(startFile);

                Assert.Equal(ancestorSln, target);
            }
            finally
            {
                File.Delete(pidPath);
            }
        }
        finally
        {
            Directory.Delete(ancestor, recursive: true);
        }
    }

    [Fact]
    public void Falls_back_to_FindTarget_result_when_no_ancestor_daemon_is_live()
    {
        var ancestor = Directory.CreateTempSubdirectory("WarmestTargetTest_").FullName;
        try
        {
            File.WriteAllText(Path.Combine(ancestor, "Umbrella.sln"), "");

            var nested = Directory.CreateDirectory(Path.Combine(ancestor, "sub-repo")).FullName;
            var nestedSln = Path.Combine(nested, "SubRepo.sln");
            File.WriteAllText(nestedSln, "");
            var startFile = Path.Combine(nested, "Foo.cs");
            File.WriteAllText(startFile, "class Foo {}");

            // No pid file written anywhere: no live ancestor daemon exists for this synthetic tree.
            var warmest = DotnetWorkspaceResolver.FindWarmestTarget(startFile);
            var plain = DotnetWorkspaceResolver.FindTarget(startFile);

            Assert.Equal(plain, warmest);
            Assert.Equal(nestedSln, warmest);
        }
        finally
        {
            Directory.Delete(ancestor, recursive: true);
        }
    }
}
