# LSP daemon architecture (host/session split)

Phase C of the tk architecture plan extracts a language-agnostic **DaemonHost**
from `Lsp/LspDaemon.cs`. The host owns everything about being a long-lived
daemon *process* that has nothing to do with LSP: sockets, PID files, child
process supervision, and shutdown. Everything that speaks the LSP protocol
(handshake, request routing, document sync) stays in `LspDaemon` — soon to be
called the "Roslyn session" once a second language backend needs its own
session type. No restructuring of the protocol/session internals happens in
this phase (deferred to the TypeScript-backend work).

## Ownership table

| Concern | Owner | Notes |
|---|---|---|
| Socket bind/accept | `DaemonHost` | Bind doubles as the cross-process single-instance lock (see below). |
| PID file write/read/cleanup | `DaemonHost` (via `DaemonSocket` low-level I/O) | `DaemonSocket` stays the low-level file/socket util; `DaemonHost` adds the higher-level policy (`CleanupOrphan`, `TryForceKill`). |
| Orphan detection & kill | `DaemonHost` | Static `DaemonHost.CleanupOrphan`/`TryForceKill`/`IsSocketLiveAsync`, called from `LspStatusCommand` instead of duplicating the logic there. |
| Child-process handle (generic) | `DaemonHost` | Takes a `Func<ProcessStartInfo>` from the caller — no language IDs, no `--extensionLogDirectory`, no Roslyn assumptions. Assumes exactly one backend process. |
| Readiness callback plumbing | `DaemonHost` (wiring) / `LspDaemon` (semantics) | Host wires `Process.Exited` and invokes the caller-supplied `OnBackendExited`/`OnBackendStarted` callbacks; what "ready" or "failed" *means* is entirely session-defined. |
| Stop / SIGTERM handling | `DaemonHost` (host-side unwind) / `LspDaemonCommand` (signal wiring, unchanged) | `RequestShutdown()` and `ReportFatalStartupFailure()` both cancel the same internal linked token that the accept loop observes. |
| Graceful-then-kill shutdown | `DaemonHost` | Single `finally` block: delete socket, delete PID file, kill process tree. |
| Socket/PID file cleanup on exit | `DaemonHost` | Same `finally` block as above. |
| LSP handshake, capabilities, `initialize`/`initialized` | `LspDaemon` (session) | Unchanged. |
| `workspace/configuration`, `client/registerCapability` answers | `LspDaemon` (session) | Unchanged. |
| `didOpen`/`didChange`/`didClose` document sync (mtime resync) | `LspDaemon` (session) | Wired in wave 2 (see below) — `DecideSyncAction` was a pure helper with unit tests but was not actually called anywhere; `EnsureFileOpenAsync` now uses it on every query. |
| Request handling (def/refs/callers/rename/symbols/diag/impl) | `LspDaemon` (session) | `diag`/`impl` added in wave 2 (see below). |
| Readiness signal detection (`projectInitializationComplete`) | `LspDaemon` (session) / `ILanguageBackend.IsReadySignal` | Unchanged. |
| `DaemonClient` / CLI command behavior | `Commands/*`, `Lsp/DaemonClient.cs` | Unchanged public behavior; `LspStatusCommand` now calls `DaemonHost` statics instead of re-implementing orphan/liveness logic inline. |

## Behavioral notes (bug fixes bundled into the extraction)

1. **Concurrent-start race (lifecycle test j).** The old code did
   `if (File.Exists(socketPath)) File.Delete(socketPath);` unconditionally
   before binding. Two daemons racing to start for the same workspace could
   both pass this check; the second one would delete the *first* daemon's
   live socket file and rebind, orphaning the first daemon (alive, unreachable).
   `DaemonHost` now acquires a dedicated `<socket>.lock` file via
   `FileMode.CreateNew` (an atomic `O_CREAT|O_EXCL` open — no TOCTOU window)
   before ever touching the socket. A first attempt at this fix used the
   socket bind itself as the lock, probing a failed bind with a connect to
   tell "live" from "stale" — but that left a real race in the gap between a
   winner's successful `bind()` and its subsequent `listen()`: a loser's probe
   landing in that gap saw "file exists but nothing answers" and wrongly
   stole the socket out from under the still-starting winner. The dedicated
   lock file removes that ambiguity: losing `CreateNew` means the lock file
   already exists; its age (vs. a 3s grace period, generous relative to the
   microseconds-wide bind-then-listen window) decides whether to stand down
   or reclaim an abandoned lock from a crashed starter.
2. **Prompt exit on unrecoverable startup failure (lifecycle tests g/h).**
   Previously, if the backend crashed before becoming ready, or the handshake
   timed out, the daemon stayed alive (reporting `Failed` to any client) for
   up to the 30-minute idle timeout. `DaemonHost.ReportFatalStartupFailure()`
   is now invoked from the same `Loading`-guarded branches that already set
   `DaemonState.Failed`, causing the accept loop to unwind and clean up
   immediately. Client-facing behavior (an immediate exception from
   `WaitForReadyAsync`) was already correct and is unchanged.

All other daemon behavior — wave-1 fixes (graceful stop → PID verify →
force-kill, stale-socket connect probe, orphan cleanup, SIGTERM handler,
`.pid` file format), document sync, and request handling — is preserved
identically.

## Wave 2: `tk diag` and `tk impl`

### Diagnostics mechanism: pull (`textDocument/diagnostic`), not push, not workspace-wide

The Roslyn server backing this daemon (`Microsoft.CodeAnalysis.LanguageServer`, resolved via
the VS Code C# extension's `.roslyn` directory or `TK_LSP_CSHARP_SERVER`) moved to the LSP
3.17 **pull diagnostics** model — its own changelog documents "Move VS Code To Pull
Diagnostics" as a deliberate migration away from server-initiated `publishDiagnostics` push.
Three mechanisms were available and considered:

1. **`textDocument/diagnostic` (per file, pull) — chosen.** Client sends the request, gated on
   advertising `capabilities.textDocument.diagnostic` in `initialize` (added to the daemon's
   capabilities alongside the existing `references`/`rename`/etc.). The daemon never sends a
   `previousResultId`, so the server always answers with a full `DocumentDiagnosticReport`
   (`{ kind: "full", items: Diagnostic[] }`) rather than `"unchanged"` — simplest correct
   behavior for a short-lived CLI invocation with no result-id cache to maintain.
2. **`workspace/diagnostic` (whole-workspace pull).** Exists in the protocol and the server's
   changelog references "workspace diagnostics", but its exact capability gating and
   partial-result-stream shape for this server version were not validated here, and a
   solution-wide pull is unnecessary complexity when per-file pull already answers both the
   single-file and project/directory-scope cases (see below).
3. **`publishDiagnostics` push (listen after `didOpen`).** Would require buffering
   asynchronous, server-initiated notifications per file and racing them against an
   after-open deadline — exactly the complexity the server's own pull migration was meant to
   avoid. Not used.

`tk diag <file>` therefore pulls one file. `tk diag <project.csproj|dir>` resolves the scope
to every `.cs` file under that directory (excluding `bin`/`obj`) and pulls them one at a time
over the same daemon connection, aggregating into one response — there is no dependency on
`workspace/diagnostic` at all. The file count is capped (`DiagCommand.DefaultMaxFiles`,
raised by `--more`) since each file is a separate pull request; narrowing the path scopes it
further. This is a rough wall-clock bound, not a diagnostics-count cap — every diagnostic
found in the (possibly capped) scope is reported.

### Freshness: wiring `DecideSyncAction` into `EnsureFileOpenAsync`

Diagnostics are only useful if they reflect the file's current on-disk content, but the
daemon's open-document tracking (`EnsureFileOpenAsync`) previously only ever sent
`textDocument/didOpen` once per URI (tracked in a `HashSet<string>`) and never resynced —
`DecideSyncAction` existed as a pure, unit-tested helper (`Lsp/LspDaemon.cs`,
`Tk.Tests/Lsp/DocSyncTests.cs`) but nothing called it. Any edit made to an already-opened
file without an intervening `dotnet build` (which doesn't touch the daemon at all) would have
been invisible to a long-lived warm daemon — a latent staleness bug affecting `refs`/`def`/
`callers`/`rename` too, just less visible there than it is for `diag`, whose entire purpose is
reacting to uncommitted edits.

`EnsureFileOpenAsync` now tracks `(Version, Mtime)` per open URI and, on every query, compares
the file's current mtime via `DecideSyncAction`: `None` (up to date) does nothing; `Close`
sends `textDocument/didClose` and forgets the URI (the file was deleted); `Change` re-reads
the file and resyncs it — see below for exactly how. The whole decide-and-notify sequence for
a URI is serialized under an async `SemaphoreSlim` (previously a raw `lock` guarded only the
`HashSet.Add` check, not the `didOpen` that followed it) so two concurrent requests for the
same file can't race a `didOpen` against a resync.

**`Change` resyncs via `didClose` + `didOpen`, not `didChange`.** The first implementation
sent a spec-legal rangeless `textDocument/didChange` (`contentChanges: [{ text }]`, no
`range` — the standard shape for whole-document/full sync). Live-tested against the actual
server (`Microsoft.CodeAnalysis.LanguageServer` from the VS Code C# extension), this crashed
it outright: `DidChangeHandler.GetUpdatedSourceText` → `ProtocolConversions.
RangeToLinePositionSpan` dereferences the content change's range unconditionally, so an
absent range throws `NullReferenceException`, which is unhandled at the top of the request
queue and takes the whole Roslyn child process down (`SIGABRT`, exit code 134) — silently
zombie-ing the daemon (it keeps reporting `running`; every subsequent query then hangs or
fails against a dead child, since nothing currently detects a post-`Ready` backend exit — a
pre-existing gap, not introduced here, and out of scope for this wave). Sending `didClose`
then a fresh `didOpen` with the new content achieves the identical "resync to current
content" outcome without going anywhere near that handler at all. Resync is already the cold
path (only fires when the file changed on disk since it was last opened), so the extra
notification is not perf-sensitive. This was caught by the mandatory live edit→diag→revert
cycle (see below) — it could not have been caught by unit tests, which is exactly why that
live check is a required part of this feature, not optional polish.

### `tk mv` namespace fix: why it is a text edit + diag loop, not a namespace rename

`tk mv` has always printed a note that moving a file across directories usually changes its
namespace and that tk does not rewrite it. That blocker predates the daemon: pre-daemon,
text-level namespace rewriting across a whole project was brittle with nothing to verify the
result. The warm daemon changes that calculus enough to revisit it — but not in the way it
first looks like it should.

This fix is the *default* behavior for a `.cs` file moved across directories, not an opt-in
flag — usage data on how agents actually call tk showed an opt-in flag would essentially never
be passed, and the goal (matching what Rider/VS already do when you drag a file to a new
folder) only holds if it happens automatically. Every precondition below is checked before any
namespace-related edit happens; if any of them fails, `tk mv` still completes the plain move and
explains in one line why the namespace wasn't touched — the move itself is never blocked by
namespace logic. `--no-fix-ns` opts back out to the old plain-move behavior; `--ns <namespace>`
pins the target namespace explicitly (bypassing only the convention-match precondition, not the
others).

**Prototyped first: does Roslyn's `textDocument/rename` work on a namespace symbol via this
daemon?** Empirically, on a real multi-project solution (32 files sharing one namespace):

1. **Yes, it renames namespaces.** Positioning on the namespace identifier in a `namespace
   Foo.Bar;`/`namespace Foo.Bar { }` declaration and sending `rename` returns a normal
   `WorkspaceEdit`: every declaration, `using`, and qualified reference across the workspace is
   rewritten. Verified live: renaming `OrderService.Domain` → `OrderService.DomainX` produced
   41 edits across all 32 files that referenced or declared that namespace.
2. **The `newName` must be a single identifier segment, not a dotted path.** Passing a
   multi-segment new name (e.g. `New.Other.Sub`) is silently accepted by the protocol call but
   the server answers with zero edits (`rename done, 0 edits in 0 files` in the daemon log) —
   no error, just a no-op. Renaming only ever replaces the one segment under the cursor (e.g.
   clicking the `Domain` segment and renaming to `DomainX` produces `OrderService.DomainX`,
   keeping the `OrderService.` prefix); there is no single LSP call that retargets a namespace to
   an arbitrary new dotted path in one shot.
3. **Critical finding — a namespace rename is workspace-wide by construction, not file-scoped.**
   The 41 edits above landed in all 32 files sharing `OrderService.Domain`, including files
   completely unrelated to the one being moved. This is correct IDE behavior (a namespace is
   the same symbol everywhere it's declared) but it is the wrong primitive for `tk mv`'s
   namespace fix: moving one file must change only that file's declared namespace plus the
   `using`s of files that reference *its* types — not every other file that happens to share the
   old namespace. Using Roslyn's namespace rename here would silently rewrite sibling files the
   user never asked to touch.

**Decision: declaration rewrite (text edit) + diag-driven `using` fixups, not LSP rename.**
`NamespaceConvention.RewriteDeclaration` (`Lsp/NamespaceConvention.cs`) replaces just the name
span of the moved file's own `namespace X;`/`namespace X { }` line — a single-line, mechanical
edit with no cross-file blast radius, so it is correct-by-construction for "change only the
moved file's namespace." Everything that might break as a result of that one change (files
that referenced the moved file's types) is discovered the same way `tk diag` already discovers
compile errors from the warm daemon: pull diagnostics for the affected project's `.cs` files
after the rewrite, collect `CS0246`/`CS0234` ("type or namespace not found"), and add a `using`
directive for the correct namespace to each affected file — the new namespace for files that
referenced the moved type from outside, and the *old* namespace for the moved file itself (it
may have relied on implicit access to sibling types that stayed behind). A second diag pull
confirms the fix landed; anything still unresolved is reported by file:line rather than
silently left broken. This mirrors wave 2's diag pull-diagnostics mechanism exactly — no new
daemon request type was needed. `UsingInserter.AddUsingIfMissing` (`Lsp/UsingInserter.cs`) is
the same kind of targeted text edit as the declaration rewrite: insert one line, don't touch
anything else in the file.

The discarded alternative (whole-namespace LSP rename) was not just riskier — it does not even
compose with `mv`'s contract. `mv` is a single-file operation; a symbol rename by definition
follows the symbol everywhere it lives. There is no `newName` trick or scoping flag on
`textDocument/rename` that narrows it to "only this file's declaration," and finding (2) rules
out driving it with a fully-qualified new name in one call regardless.

**Scope limitation.** The diag pull for the namespace fix is scoped to the destination
project's directory (mirroring `tk diag <project>`'s own scoping and file cap), not the whole
solution — so a reference to the moved type from a *different* project than the one it now
lives in will not be auto-patched. This is a deliberate v1 bound consistent with `tk diag`'s
existing per-project scope and cap; cross-project fixups are left to the existing
note-and-manual-fix path (the plain move-only note, printed whenever the fix is skipped or
degrades).

**Default-on, precondition-gated, never blocking.** `MvCommand.TryFixNamespaceAsync`
(`Commands/MvCommand.cs`) checks, in order, before touching any file: the lsp module is
enabled, a workspace root resolves, the moved file's original location has an owning
`.csproj`, the moved file has a parseable namespace declaration, and (unless `--ns` overrides
it) the file's current namespace already matches its *old* path's convention — the last check
exists because if a file's namespace was already hand-rolled and didn't follow the folder
convention, there's no reliable way to guess what the *new* namespace should be without asking.
Any failure here returns a `Degraded` outcome: the move (already done via `git mv` by this
point) is left exactly as it would have been without the feature, plus one line saying why
("lsp module disabled", "current namespace 'X' doesn't match path convention 'Y' — pass --ns
<target> to set it explicitly", etc.) — never a non-zero exit code, since the user's actual
request (move the file) still succeeded. Only once every precondition passes does the
declaration actually get rewritten; failures *after* that point (diag timeout, unresolved
references remaining after the patch loop) are reported as real failures (non-zero exit,
itemized remaining errors) rather than silently degraded, since at that point the file's
namespace has already changed and needs attention.

### `tk impl`

`textDocument/implementation` returns the same `Location`/`LocationLink` result shape as
`textDocument/definition`, so `FindDefinitionAsync` and the new `FindImplementationsAsync`
share one result parser (`ParseLocationOrLinkResult`). Symbol-name resolution, ambiguity
handling (`Candidates`), and the not-found error shape are identical to `def`/`refs`/
`callers` — `impl` reuses the existing `DaemonResponse.Locations` field, no new protocol
field needed (unlike `diag`, which added `DaemonRequest.Paths` and
`DaemonResponse.DiagnosticsByFile`).
