using DebugBundle;
using DebugBundle.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DebugBundle.AspNetCore.Tests;

public sealed class MiddlewareTests
{
    [Fact]
    public async Task Middleware_Captures_Request_Metadata_And_Rethrows_Exceptions()
    {
        var fakeClient = new FakeClient();
        using var server = new TestServer(new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton<IDebugBundleClient>(fakeClient);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseDebugBundle();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/orders/{id}", (HttpContext _) => Results.StatusCode(503));
                    endpoints.MapGet("/boom", (HttpContext _) => throw new InvalidOperationException("failed"));
                });
            }));

        var request = new HttpRequestMessage(HttpMethod.Get, "/orders/123");
        request.Headers.TryAddWithoutValidation("X-DebugBundle-Trace-Id", "trace_abc");
        var response = await server.CreateClient().SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var captured = Assert.Single(fakeClient.Requests);
        Assert.Equal("GET", captured.Request.Method);
        Assert.Equal("/orders/123", captured.Request.Path);
        Assert.Equal("/orders/{id}", captured.Request.RouteTemplate);
        Assert.Equal(503, captured.Response!.StatusCode);

        await Assert.ThrowsAsync<InvalidOperationException>(() => server.CreateClient().GetAsync("/boom"));
        Assert.Single(fakeClient.Exceptions);
    }
}
