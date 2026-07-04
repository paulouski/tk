using Xunit;

namespace Tk.Tests;

/// <summary>
/// Groups tests that mutate the process-wide HOME environment variable (which
/// <c>TkPaths.Root()</c> resolves paths from) together with tests that read/write files
/// under that same root over a non-trivial window (e.g. daemon lifecycle tests awaiting
/// real child processes). Without this, xunit's default parallel test-collection execution
/// can interleave a HOME-mutating test with one of these, causing the latter to compute a
/// socket/PID path against a HOME value that changes out from under it mid-test.
///
/// This is a narrow, targeted fix for flakiness the phase-C DaemonHost lifecycle tests
/// exposed (they hold real child processes open across await points, widening the
/// interleaving window); it is not the general "serialize all env-var tests" isolation
/// pass tracked separately for a later phase.
/// </summary>
[CollectionDefinition("HomeSensitive", DisableParallelization = true)]
public class HomeSensitiveCollection;
