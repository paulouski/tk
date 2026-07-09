using Xunit;

namespace Tk.Tests;

/// <summary>
/// Groups tests that mutate the process-wide TK_RAW_DIR environment variable
/// (<c>RawOutputStoreTests</c>) together with tests that indirectly read it via
/// <see cref="Tk.Common.RawOutputStore.ResolveDir"/> without overriding it themselves
/// (<c>OutputContractFooterInvariantTests</c>). Without this, xunit's default parallel
/// test-collection execution can interleave a TK_RAW_DIR-mutating test with one of these,
/// causing the latter to resolve a store directory that the former is concurrently
/// creating/deleting — surfacing as "path already exists" from <c>Directory.CreateDirectory</c>.
/// Mirrors <see cref="HomeSensitiveCollection"/>'s fix for the same class of bug with HOME.
/// </summary>
[CollectionDefinition("RawOutputStoreEnv", DisableParallelization = true)]
public class RawOutputStoreEnvCollection;
