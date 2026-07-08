using Tk.Lsp.Protocol;

namespace Tk.Lsp;

/// <summary>
/// Carries the dependencies a request handler needs from the owning <see cref="LspDaemon"/>,
/// without handing handlers a reference to the daemon itself: the live <see cref="MessageLoop"/>,
/// readiness gating, document-open/sync, and logging. Constructed per client connection by
/// <see cref="LspDaemon"/> and passed to <see cref="IRequestHandler.HandleAsync"/>.
/// </summary>
internal sealed record LspDaemonContext(
    MessageLoop Loop,
    Func<CancellationToken, Task> WaitForReadyAsync,
    Func<string, string, CancellationToken, Task> EnsureFileOpenAsync,
    Action<string> Log);

/// <summary>
/// A single LSP query method handler dispatched by <see cref="LspDaemon"/>'s request loop.
/// Implementations are registered in the daemon's dispatch table by <c>DaemonRequest.Method</c>
/// and own one method's protocol flow (the corresponding <c>Find*Async</c> + JSON parsing) end
/// to end. Returns the response to be written back to the client by the dispatcher.
/// </summary>
internal interface IRequestHandler
{
    /// <summary>
    /// Handles the request and returns the response for the dispatcher to serialize+write.
    /// </summary>
    Task<DaemonResponse> HandleAsync(LspDaemonContext ctx, DaemonRequest request, CancellationToken ct);
}
