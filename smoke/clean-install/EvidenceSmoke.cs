using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DebugBundle;
using DebugBundle.Transport;

internal static class EvidenceSmoke
{
    internal static async Task RunAsync()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var receive = ReceiveAsync(listener);
        var endpoint = new Uri($"http://127.0.0.1:{port}/v1/events"); using var transport = new HttpEventTransport(endpoint, TimeSpan.FromSeconds(5)); var hooks = 0;
        using var client = DebugBundleClient.Create(new DebugBundleOptions
        {
            ProjectMode = DebugBundleProjectMode.LocalOnly, ProjectToken = "dbundle_proj_evidence_fixture", Transport = transport,
            Service = "evidence-fixture", Environment = "test", FlushInterval = TimeSpan.FromHours(1), CaptureRecoveryObservations = true,
            EvidenceCapabilities = new EvidenceCapabilities { Features = new[] { "exception_evidence_v1", "recovery_v1" } },
            BeforeSend = envelope => { hooks++; envelope.EventId = Guid.NewGuid().ToString("D"); return envelope; }
        });
        var failure = Guid.NewGuid().ToString("D"); var attempt = Guid.NewGuid().ToString("D"); Exception? retained = null;
        await client.RunWithOperationContextAsync(new DebugBundleOperationOptions { Kind = "job", Name = "fixture.run", Limit = TimeSpan.FromSeconds(2) }, async () =>
        {
            await Task.Yield();
            try { throw new InvalidOperationException("fictional root", new ArgumentException("password=EVIDENCE_PACKAGE_CANARY")); }
            catch (Exception error) { retained = error; client.CaptureException(error, EvidenceContext.ForFailure(failure)); }
            client.CaptureRecovery(new RecoveryObservation { FailureRef = failure, AttemptId = attempt, State = "attempt_completed", StatusCode = 200 });
            client.CaptureRecovery(new RecoveryObservation { FailureRef = failure, AttemptId = attempt, State = "recovery_observed" });
        });
        await client.FlushAsync(); var body = await receive.WaitAsync(TimeSpan.FromSeconds(10)); GC.KeepAlive(retained);
        if (body.Contains("EVIDENCE_PACKAGE_CANARY", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(body) > 262144) throw new InvalidOperationException("Installed evidence privacy or native byte bound failed.");
        using var document = JsonDocument.Parse(body); var events = document.RootElement.GetProperty("events").EnumerateArray().ToArray();
        if (events.Length != 3 || hooks != 3) throw new InvalidOperationException("Installed explicit evidence flow lost observations or repeated hooks.");
        var original = events.Single(item => item.GetProperty("event_type").GetString() == "backend_exception"); var payload = original.GetProperty("payload"); var nodes = payload.GetProperty("exception_evidence").GetProperty("nodes");
        if (nodes.GetArrayLength() != 2 || nodes[0].GetProperty("frames").GetArrayLength() == 0 || payload.GetProperty("execution").GetProperty("kind").GetString() != "job") throw new InvalidOperationException("Installed native causes, frames or operation context missing.");
        var reference = original.GetProperty("context").GetProperty("evidence_reference"); var observations = events.Where(item => item.GetProperty("event_type").GetString() == "diagnostic_context").ToArray();
        if (reference.GetProperty("failure_ref").GetString() != failure || observations[0].GetProperty("payload").GetProperty("state").GetString() != "attempt_completed" || observations[1].GetProperty("payload").GetProperty("state").GetString() != "recovery_observed") throw new InvalidOperationException("Installed explicit recovery semantics changed.");
        foreach (var observation in observations)
            if (observation.GetProperty("payload").GetProperty("scope").GetRawText() != reference.GetProperty("scope").GetRawText()) throw new InvalidOperationException("Installed recovery scope association failed.");
        Console.WriteLine("Clean installed package native cause/frame/operation/recovery/privacy/ACK flow passed.");
    }
    private static async Task<string> ReceiveAsync(HttpListener listener)
    {
        var context = await listener.GetContextAsync();
        if (context.Request.HttpMethod != "POST" || context.Request.Headers["X-DebugBundle-Evidence"] != "1" || context.Request.Headers["Authorization"] != "Bearer dbundle_proj_evidence_fixture") throw new InvalidOperationException("Installed native evidence transport identity failed.");
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding); var body = await reader.ReadToEndAsync(); using var document = JsonDocument.Parse(body);
        var response = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { accepted = document.RootElement.GetProperty("events").GetArrayLength(), rejected = 0, errors = Array.Empty<object>(), evidence_capabilities = new { version = 1, features = new[] { "exception_evidence_v1", "recovery_v1" } } }));
        context.Response.StatusCode = 202; context.Response.ContentType = "application/json"; context.Response.ContentLength64 = response.Length; await context.Response.OutputStream.WriteAsync(response); context.Response.Close(); return body;
    }
}
