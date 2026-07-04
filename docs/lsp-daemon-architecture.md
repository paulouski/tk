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

### `tk impl`

`textDocument/implementation` returns the same `Location`/`LocationLink` result shape as
`textDocument/definition`, so `FindDefinitionAsync` and the new `FindImplementationsAsync`
share one result parser (`ParseLocationOrLinkResult`). Symbol-name resolution, ambiguity
handling (`Candidates`), and the not-found error shape are identical to `def`/`refs`/
`callers` — `impl` reuses the existing `DaemonResponse.Locations` field, no new protocol
field needed (unlike `diag`, which added `DaemonRequest.Paths` and
`DaemonResponse.DiagnosticsByFile`).
