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
| `didOpen` / document sync (mtime resync) | `LspDaemon` (session) | Unchanged (`DecideSyncAction` pure helper). |
| Request handling (def/refs/callers/rename/symbols) | `LspDaemon` (session) | Unchanged. |
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
