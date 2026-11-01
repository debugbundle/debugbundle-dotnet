using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DebugBundle;
using DebugBundle.Transport;

const int pairs = 20;
var features = new[] { "exception_evidence_v1", "recovery_v1" };
for (var repeat = 0; repeat < 4; repeat++)
    foreach (var evidence in new[] { false, true })
        foreach (var pressure in new[] { false, true })
        {
            var transport = new MemoryTransport(features);
            using var client = DebugBundleClient.Create(new DebugBundleOptions
            {
                ProjectMode = DebugBundleProjectMode.LocalOnly, Environment = "local",
                Transport = transport, BatchSize = 1001, FlushInterval = TimeSpan.FromHours(1),
                RequestTimeout = TimeSpan.FromMinutes(2), CaptureExceptionEvidence = evidence,
                CaptureRecoveryObservations = evidence, EvidenceCapabilities = new EvidenceCapabilities { Features = features }
            });
            // Retain real queue ownership while preventing automatic transport from
            // draining the pressure fixture. Reflection is confined to this harness.
            Set(client, "_retryUntil", DateTimeOffset.UtcNow.AddHours(1));
            var sync = Get<object>(client, "_sync");
            if (pressure)
                lock (sync)
                    for (var index = 0; index < 512; index++)
                        client.CaptureLog($"retained warning {index}", DebugBundleLogLevel.Warning);
            await client.FlushAsync();
            var errors = Enumerable.Range(0, pairs).Select(NativeError).ToArray();
            var scope = new EvidenceScope { OperationId = Guid.NewGuid().ToString("D") };
            var contexts = errors.Select(_ => EvidenceContext.ForFailure(Guid.NewGuid().ToString("D"), scope)).ToArray();
            var observations = contexts.Select(context => new RecoveryObservation
            {
                FailureRef = (string)context["failure_ref"]!, Scope = scope,
                AttemptId = Guid.NewGuid().ToString("D"), State = "attempt_completed", StatusCode = 200
            }).ToArray();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long captureTicks, captureBytes;
            lock (sync)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                for (var index = 0; index < pairs; index++)
                {
                    client.CaptureException(errors[index], contexts[index]);
                    client.CaptureRecovery(observations[index]);
                }
                captureTicks = Stopwatch.GetTimestamp() - started;
                captureBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            }
            await client.FlushAsync(); // Drain scheduling only; the retry guard retains events.
            var queued = Get<List<DebugBundleEventEnvelope>>(client, "_buffer");
            var retainedCount = queued.Count;
            var retainedBytes = Get<int>(client, "_queuedBytes");
            var budget = Get<object>(client, "_diagnostics");
            var diagnosticBytes = (int)budget.GetType().GetProperty("Bytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(budget)!;
            Require(queued.Count(item => item.EventType == "backend_exception") == pairs, "primary queue loss");
            Require(retainedCount <= 512 && retainedBytes <= 8 * 1024 * 1024 && diagnosticBytes <= 65536, "retained budget");
            Set(client, "_retryUntil", null);
            // This process contains only the harness and SDK. Total allocations
            // include the native asynchronous sender and ordinary runtime overhead.
            var processAllocated = GC.GetTotalAllocatedBytes(true);
            var flushStarted = Stopwatch.GetTimestamp();
            await client.FlushAsync();
            var flushTicks = Stopwatch.GetTimestamp() - flushStarted;
            var flushBytes = GC.GetTotalAllocatedBytes(true) - processAllocated;
            var primary = transport.Events.Where(item => item.EventType == "backend_exception").ToArray();
            Require(primary.Length == pairs, "primary delivery loss");
            foreach (var item in primary)
            {
                Require(item.Payload.ContainsKey("exception_evidence") == evidence, "evidence option mismatch");
                if (evidence)
                {
                    using var graph = JsonDocument.Parse(JsonSerializer.Serialize(item.Payload["exception_evidence"]));
                    Require(graph.RootElement.GetProperty("nodes").GetArrayLength() == 2, "native cause missing");
                }
            }
            Require(transport.Events.Count(item => item.EventType == "diagnostic_context") == (evidence && !pressure ? pairs : 0), "diagnostic priority");
            GC.KeepAlive(errors);
            if (repeat > 0)
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    repeat, runtime = Environment.Version.ToString(), evidence, pressure, pairs,
                    capture_ns_per_pair = captureTicks * 1e9 / Stopwatch.Frequency / pairs,
                    capture_allocated_bytes_per_pair = captureBytes / (double)pairs,
                    deferred_flush_ns = flushTicks * 1e9 / Stopwatch.Frequency,
                    deferred_process_allocated_bytes = flushBytes,
                    retained_count = retainedCount, retained_wire_bytes = retainedBytes, diagnostic_bytes = diagnosticBytes
                }));
        }

static Exception NativeError(int index)
{
    try { try { throw new InvalidOperationException($"native cause {index}"); } catch (Exception inner) { throw new Exception($"evidence primary {index}", inner); } }
    catch (Exception error) { return error; }
}
static T Get<T>(DebugBundleClient client, string name) => (T)typeof(DebugBundleClient).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
static void Set(DebugBundleClient client, string name, object? value) => typeof(DebugBundleClient).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, value);
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

sealed class MemoryTransport(string[] features) : IEventTransport
{
    public List<DebugBundleEventEnvelope> Events { get; } = new();
    public Task<EventTransportResult> SendAsync(EventTransportRequest request, CancellationToken cancellationToken = default)
    {
        Events.AddRange(request.Events);
        return Task.FromResult(new EventTransportResult
        {
            StatusCode = 202,
            Body = JsonSerializer.Serialize(new { accepted = request.Events.Count, rejected = 0, errors = Array.Empty<object>(), evidence_capabilities = new { version = 1, features } })
        });
    }
}
