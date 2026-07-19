using System.Collections.Concurrent;
using Tk.Lsp.Protocol;

namespace Tk.Lsp;

/// <summary>
/// Forwards on-disk create/change/delete events for the backend's file/workspace-marker
/// extensions (e.g. .cs/.csproj/.sln) to the server via <c>workspace/didChangeWatchedFiles</c>
/// — the standard LSP mechanism a server relies on to learn about files it was never asked to
/// open: a new type added by a parallel process, a file renamed/moved, or an untouched
/// dependency edited on disk. Roslyn's language server does not watch the filesystem itself
/// (confirmed via its own LspFileChangeWatcher: it dynamically registers for this notification
/// and depends entirely on the client sending it); <see cref="DocumentSync"/> only resyncs
/// documents tk itself has previously opened, which leaves a dependency the daemon never
/// queried permanently stale from Roslyn's point of view — the phantom CS0246/CS0234 this
/// class exists to fix.
///
/// Backed by a <see cref="FileSystemWatcher"/> (push-based; no per-request directory scan).
/// Pending events are coalesced by path in <see cref="_pending"/> and flushed as one
/// notification right before each request, from the same choke point
/// <see cref="DocumentSync.RefreshOpenDocumentsAsync"/> already runs from
/// (<c>LspDaemon.WaitForReadyAndRefreshAsync</c>). <see cref="_flushLock"/> makes a flush
/// atomic against concurrent callers: only the caller that actually drains
/// <see cref="_pending"/> sends anything, so two requests refreshing at once neither duplicate
/// nor drop a file's event.
///
/// On a watcher buffer overflow (the one case where FileSystemWatcher itself admits it may
/// have silently dropped events), the coalesced set can no longer be trusted, so recovery is a
/// full rescan-and-diff against the last known file listing — extra correctness over trusting a
/// possibly-incomplete queue.
/// </summary>
internal sealed class WatchedFileTracker : IDisposable
{
    private readonly string _root;
    private readonly string[] _extensions;
    private readonly Action<string> _log;
    private readonly FileSystemWatcher? _watcher;
    private readonly SemaphoreSlim _flushLock = new(1, 1);

    // Path -> most recently observed change. The version distinguishes two concurrent events
    // of the same kind so draining a snapshot cannot remove a newer update for the same path.
    // Concurrent because FileSystemWatcher raises events on ThreadPool threads independent of
    // FlushAsync.
    private readonly ConcurrentDictionary<string, PendingChange> _pending = new(StringComparer.Ordinal);
    private long _nextPendingVersion;
    private volatile bool _overflowed;

    // Snapshot of watched file paths, consulted only on overflow recovery (diff-based rescan)
    // — not on the normal path, so a healthy watcher never pays for a directory walk.
    private HashSet<string> _knownFiles;

    internal WatchedFileTracker(string root, string[] extensions, Action<string> log)
    {
        _root = root;
        _extensions = [.. extensions];
        _log = log;
        _knownFiles = EnumerateWatchedFiles(_extensions);

        // A nonexistent root (e.g. a workspace that hasn't been created yet) can't be watched;
        // FileSystemWatcher's constructor throws for it. Degrade to a no-op tracker rather than
        // fail daemon startup over what the rest of the daemon already treats as a plain "no
        // files here yet" case.
        if (!Directory.Exists(root))
        {
            _watcher = null;
            return;
        }

        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };
        foreach (var ext in _extensions)
            watcher.Filters.Add($"*{ext}");

        watcher.Created += (_, e) => Enqueue(e.FullPath, WatcherChangeTypes.Created);
        watcher.Deleted += (_, e) => Enqueue(e.FullPath, WatcherChangeTypes.Deleted);
        watcher.Changed += (_, e) => Enqueue(e.FullPath, WatcherChangeTypes.Changed);
        watcher.Renamed += (_, e) =>
        {
            Enqueue(e.OldFullPath, WatcherChangeTypes.Deleted);
            Enqueue(e.FullPath, WatcherChangeTypes.Created);
        };
        watcher.Error += (_, e) =>
        {
            _overflowed = true;
            _log($"watched-file tracker: overflow ({e.GetException().Message}), next flush does a full rescan");
        };

        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
    }

    private void Enqueue(string path, WatcherChangeTypes type)
    {
        if (IsExcluded(path)) return;
        _pending[path] = new PendingChange(type, Interlocked.Increment(ref _nextPendingVersion));
    }

    /// <summary>
    /// Drains pending watched-file events (or, after an overflow, does a full rescan-and-diff)
    /// and sends them as one <c>workspace/didChangeWatchedFiles</c> notification. No-op when
    /// nothing changed, and when the tracker degraded to no-op at construction time.
    /// </summary>
    internal async Task FlushAsync(MessageLoop loop, CancellationToken ct)
    {
        if (_watcher is null) return;

        await _flushLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<(string Path, WatcherChangeTypes Type)> events;

            if (_overflowed)
            {
                _overflowed = false;
                _pending.Clear();
                events = DiffAgainstKnown();
            }
            else
            {
                if (_pending.IsEmpty) return;

                var snapshot = _pending.ToArray();
                PendingSnapshotTakenForTest?.Invoke();

                events = [];
                var pendingEntries = (ICollection<KeyValuePair<string, PendingChange>>)_pending;
                foreach (var entry in snapshot)
                {
                    // Remove only the exact version that was snapshotted. If the watcher
                    // concurrently records a newer event for the same path, send the
                    // snapshotted event now and leave the newer one queued for the next flush.
                    pendingEntries.Remove(entry);
                    events.Add((entry.Key, entry.Value.Type));
                }
                foreach (var (path, type) in events)
                {
                    if (type == WatcherChangeTypes.Deleted) _knownFiles.Remove(path);
                    else _knownFiles.Add(path);
                }
            }

            if (events.Count == 0) return;

            var changes = events.Select(e => new
            {
                uri = new Uri(e.Path).ToString(),
                type = e.Type switch
                {
                    WatcherChangeTypes.Created => 1,
                    WatcherChangeTypes.Deleted => 3,
                    _ => 2, // Changed
                }
            }).ToArray();

            await loop.SendNotificationAsync("workspace/didChangeWatchedFiles",
                new { changes }, ct).ConfigureAwait(false);
            _log($"didChangeWatchedFiles: {changes.Length} file(s)");
        }
        finally
        {
            _flushLock.Release();
        }
    }

    // Overflow recovery: a full filename-only rescan (no content reads) diffed against the
    // last known listing. Every currently-existing watched file is reported — as Changed if it
    // was already known (an overflow means we cannot rule out a missed content edit) or
    // Created if it wasn't — plus a Deleted for every previously-known file that vanished.
    private List<(string Path, WatcherChangeTypes Type)> DiffAgainstKnown()
    {
        var current = EnumerateWatchedFiles(_extensions);

        var events = new List<(string, WatcherChangeTypes)>();
        foreach (var path in current)
            events.Add((path, _knownFiles.Contains(path) ? WatcherChangeTypes.Changed : WatcherChangeTypes.Created));
        foreach (var path in _knownFiles)
            if (!current.Contains(path))
                events.Add((path, WatcherChangeTypes.Deleted));

        _knownFiles = current;
        _log($"watched-file tracker: overflow recovery rescan, {events.Count} file(s)");
        return events;
    }

    private HashSet<string> EnumerateWatchedFiles(string[] extensions)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(_root)) return result;

        foreach (var ext in extensions)
        {
            foreach (var f in Directory.EnumerateFiles(_root, $"*{ext}", SearchOption.AllDirectories))
            {
                if (!IsExcluded(f)) result.Add(f);
            }
        }
        return result;
    }

    private bool IsExcluded(string path)
    {
        var relative = Path.GetRelativePath(_root, path);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p is "bin" or "obj");
    }

    // For testing: observe the live watcher's queue without racing a real FlushAsync, and
    // force the overflow-recovery path without needing to actually overrun the OS watch
    // buffer.
    internal int PendingCountForTest => _pending.Count;
    internal void ForceOverflowForTest() => _overflowed = true;
    internal void EnqueueForTest(string path, WatcherChangeTypes type) => Enqueue(path, type);
    internal Action? PendingSnapshotTakenForTest { get; set; }

    private readonly record struct PendingChange(WatcherChangeTypes Type, long Version);

    public void Dispose()
    {
        _watcher?.Dispose();
        _flushLock.Dispose();
    }
}
