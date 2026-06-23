using System.Text.Json;
using Tk.Lsp;
using Tk.Lsp.Protocol;
using Xunit;

namespace Tk.Tests.Lsp;

public class WorkspaceReadyTests
{
    private static LspIncoming MakeProgressMsg(string token, string kind) =>
        LspMessage.Parse(
            "{\"jsonrpc\":\"2.0\",\"method\":\"$/progress\",\"params\":{\"token\":\"" + token +
            "\",\"value\":{\"kind\":\"" + kind + "\",\"message\":\"info\"}}}");

    private static LspIncoming MakeOtherMsg(string method) =>
        LspMessage.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"" + method + "\",\"params\":{}}");

    private readonly CSharpBackend _backend = new();

    [Fact]
    public void WorkspaceReady_end_is_recognized()
    {
        var msg = MakeProgressMsg("WorkspaceReady", "end");
        Assert.True(_backend.IsReadySignal(msg));
    }

    [Fact]
    public void WorkspaceReady_begin_is_not_ready()
    {
        var msg = MakeProgressMsg("WorkspaceReady", "begin");
        Assert.False(_backend.IsReadySignal(msg));
    }

    [Fact]
    public void WorkspaceReady_report_is_not_ready()
    {
        var msg = MakeProgressMsg("WorkspaceReady", "report");
        Assert.False(_backend.IsReadySignal(msg));
    }

    [Fact]
    public void Other_token_end_is_not_ready()
    {
        var msg = MakeProgressMsg("SomethingElse", "end");
        Assert.False(_backend.IsReadySignal(msg));
    }

    [Fact]
    public void Non_progress_method_is_not_ready()
    {
        var msg = MakeOtherMsg("window/logMessage");
        Assert.False(_backend.IsReadySignal(msg));
    }

    [Fact]
    public void Notification_without_params_is_not_ready()
    {
        var msg = LspMessage.Parse("""{"jsonrpc":"2.0","method":"$/progress"}""");
        Assert.False(_backend.IsReadySignal(msg));
    }
}
