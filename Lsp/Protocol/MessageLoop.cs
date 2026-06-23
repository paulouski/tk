using System.Collections.Concurrent;
using System.Text.Json;

namespace Tk.Lsp.Protocol;

/// <summary>
/// Async concurrent LSP message loop.
/// Reads framed messages from the server and dispatches them to handlers or pending request completions.
/// </summary>
public sealed class MessageLoop : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, Func<int, JsonElement?, Task<object?>>> _handlers = new();
    private readonly List<(Predicate<LspIncoming> Match, TaskCompletionSource<LspIncoming> Tcs)> _watchers = [];
    private readonly object _watchersLock = new();
    private readonly Task _readerTask;
    private readonly CancellationTokenSource _cts = new();
    private int _nextId = 1;

    /// <summary>Optional diagnostic tap: invoked with a short description of every incoming message.</summary>
    public Action<string>? Trace { get; set; }

    public MessageLoop(Stream inputStream, Stream outputStream)
    {
        _input = inputStream;
        _output = outputStream;
        _readerTask = Task.Run(ReadLoopAsync);
    }

    /// <summary>
    /// Registers a handler for server-initiated requests with the given method.
    /// The handler receives the request id and params, and returns the result object.
    /// </summary>
    public void RegisterHandler(string method, Func<int, JsonElement?, Task<object?>> handler)
    {
        _handlers[method] = handler;
    }

    /// <summary>
    /// Sends a request to the server and waits for its response.
    /// </summary>
    public async Task<JsonElement> SendRequestAsync(string method, object? @params, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new LspRequest("2.0", id, method, @params);
        await WriteFrameAsync(LspMessage.Serialize(request), ct).ConfigureAwait(false);

        using var reg = ct.Register(() =>
        {
            _pending.TryRemove(id, out _);
            tcs.TrySetCanceled(ct);
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a notification to the server (no response expected).
    /// </summary>
    public async Task SendNotificationAsync(string method, object? @params, CancellationToken ct = default)
    {
        var notification = new LspNotification("2.0", method, @params);
        await WriteFrameAsync(LspMessage.Serialize(notification), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until a notification matching the predicate is received.
    /// </summary>
    public Task<LspIncoming> WaitForNotificationAsync(Predicate<LspIncoming> match, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<LspIncoming>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_watchersLock)
        {
            _watchers.Add((match, tcs));
        }

        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    private async Task ReadLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var json = await LspFrame.ReadNextAsync(_input, ct).ConfigureAwait(false);
                if (json is null)
                    break;

                LspIncoming msg;
                try
                {
                    msg = LspMessage.Parse(json);
                }
                catch
                {
                    continue;
                }

                if (Trace is not null)
                {
                    if (msg.id.HasValue && msg.method is null)
                        Trace($"response id={msg.id}");
                    else if (msg.id.HasValue)
                        Trace($"request method={msg.method} id={msg.id}");
                    else if (msg.method == "$/progress" && msg.@params is { } tp)
                        Trace($"notification $/progress params={tp.GetRawText()}");
                    else if (msg.method == "window/logMessage" && msg.@params is { } lp)
                        Trace($"notification window/logMessage params={lp.GetRawText()}");
                    else
                        Trace($"notification method={msg.method}");
                }

                // Response to a request we sent
                if (msg.id.HasValue && msg.method is null)
                {
                    if (_pending.TryRemove(msg.id.Value, out var tcs))
                    {
                        if (msg.error.HasValue)
                            tcs.TrySetException(new InvalidOperationException($"LSP error: {msg.error}"));
                        else
                            tcs.TrySetResult(msg.result ?? default);
                    }
                    continue;
                }

                // Server-initiated request (has id and method)
                if (msg.id.HasValue && msg.method is not null)
                {
                    var reqMethod = msg.method;
                    var reqId = msg.id.Value;
                    _ = Task.Run(async () =>
                    {
                        object? result = null;
                        if (_handlers.TryGetValue(reqMethod, out var handler))
                        {
                            try { result = await handler(reqId, msg.@params).ConfigureAwait(false); }
                            catch (Exception ex) { Trace?.Invoke($"handler EXCEPTION method={reqMethod}: {ex}"); }
                        }
                        else
                        {
                            Trace?.Invoke($"no handler for server request method={reqMethod}, answering null");
                        }

                        var response = new LspResponse("2.0", reqId, result);
                        try
                        {
                            await WriteFrameAsync(LspMessage.Serialize(response), ct).ConfigureAwait(false);
                            Trace?.Invoke($"answered server request method={reqMethod} id={reqId}");
                        }
                        catch (Exception ex) { Trace?.Invoke($"WRITE EXCEPTION answering method={reqMethod}: {ex}"); }
                    }, ct);
                    continue;
                }

                // Notification (no id)
                List<(Predicate<LspIncoming> Match, TaskCompletionSource<LspIncoming> Tcs)> toCheck;
                lock (_watchersLock)
                {
                    toCheck = [.. _watchers];
                }

                foreach (var (match, tcs) in toCheck)
                {
                    if (match(msg))
                    {
                        lock (_watchersLock)
                        {
                            _watchers.Remove((match, tcs));
                        }
                        tcs.TrySetResult(msg);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* stream closed */ }
        finally
        {
            // Complete all pending with cancellation
            foreach (var (_, tcs) in _pending)
                tcs.TrySetCanceled();
        }
    }

    private async Task WriteFrameAsync(string json, CancellationToken ct)
    {
        var bytes = LspFrame.Encode(json);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _readerTask.ConfigureAwait(false); } catch { }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
