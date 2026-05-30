using System.Text.Json;
using DebugBundle;
using DebugBundle.Transport;

namespace DebugBundle.Sdk.Tests;

public sealed class FileTransportTests
{
    [Fact]
    public async Task File_Transport_Writes_Atomic_Event_Array()
    {
        var root = Path.Combine(Path.GetTempPath(), "debugbundle-dotnet-tests", Guid.NewGuid().ToString("N"));
        var transport = new FileEventTransport(root);

        var result = await transport.SendAsync(new EventTransportRequest
        {
            ProjectToken = "dbundle_proj_test",
            Events = new[]
            {
                new DebugBundleEventEnvelope
                {
                    EventType = "log_event",
                    Service = new DebugBundleServiceDescriptor { Name = "checkout-api", Environment = "test" },
                    Payload = new Dictionary<string, object?> { ["message"] = "hello" }
                }
            }
        });

        Assert.True(File.Exists(result.WrittenFilePath));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.WrittenFilePath!));
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.EndsWith("-checkout-api.events.json", result.WrittenFilePath);
    }
}
