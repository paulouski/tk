using Tk.Lsp;
using Xunit;

namespace Tk.Tests.Lsp;

public class SymbolResolverMessageTests
{
    /// <summary>
    /// workspace/symbol only indexes the workspace's own sources, so an external (NuGet/BCL)
    /// symbol comes back empty exactly like a nonexistent one. The message must not claim the
    /// symbol does not exist, and must name the position form that does resolve such symbols.
    /// </summary>
    [Fact]
    public void Not_found_message_scopes_the_claim_and_points_at_the_position_form()
    {
        var msg = SymbolResolver.NotFoundMessage("IServiceCollection", "impl");

        Assert.Contains("'IServiceCollection'", msg);
        Assert.Contains("workspace sources", msg);
        Assert.Contains("tk impl <file:line:col>", msg);
    }
}
