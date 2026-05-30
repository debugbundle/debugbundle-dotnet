using DebugBundle.AspNetCore;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace DebugBundle.AspNetCore.Tests;

public sealed class BlazorCircuitHandlerTests
{
    [Fact]
    public async Task Inbound_Handler_Captures_Exception_And_Rethrows()
    {
        var client = new FakeClient();
        var handler = new DebugBundleCircuitHandler(client);
        var exception = new InvalidOperationException("failed");
        var wrapped = handler.CreateInboundActivityHandler(_ => throw exception);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => wrapped(null!));

        Assert.Same(exception, thrown);
        var captured = Assert.Single(client.Exceptions);
        Assert.Equal("blazor-server", captured.Context!["framework"]);
    }
}
