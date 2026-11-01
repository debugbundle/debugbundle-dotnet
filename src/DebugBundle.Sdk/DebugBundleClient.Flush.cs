using DebugBundle.Redaction;
using DebugBundle.Transport;

namespace DebugBundle;

public sealed partial class DebugBundleClient
{
    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        List<DebugBundleEventEnvelope>? batchToRestore = null;
        Dictionary<DebugBundleEventEnvelope, int> batchSizes = new();
        Dictionary<string, PressureCount> pressure;
        try
        {
            IEventTransport? transport;
            List<DebugBundleEventEnvelope> batch;
            lock (_sync)
            {
                if (_disposed || _transport == null)
                {
                    return;
                }

                if (_retryUntil != null && DateTimeOffset.UtcNow < _retryUntil.Value)
                {
                    Status = DebugBundleStatus.Degraded;
                    ScheduleRetryLocked();
                    return;
                }

                transport = _transport;
                batch = new List<DebugBundleEventEnvelope>(_buffer);
                batchToRestore = batch;
                batchSizes = new Dictionary<DebugBundleEventEnvelope, int>(_queuedSizes);
                MoveQueueToInFlightLocked();
                pressure = DrainPressureLocked();
                _flushTimer?.Dispose();
                _flushTimer = null;
            }

            for (var index = 0; index < batch.Count;)
            {
                var captured = batch[index];
                var safe = PrepareQueuedEvent(captured);
                var bytes = safe == null ? 0 : EventBytes(safe);
                lock (_sync)
                {
                    ReleasePreparedOriginalLocked(captured, batchSizes[captured]);
                    batchSizes.Remove(captured);
                    if (!_disposed && safe != null && ReconcileDiagnosticLocked(captured, safe, bytes))
                    {
                        if (ReservePreparedLocked(safe, bytes))
                        {
                            RegisterPrimaryLocked(safe);
                            batch[index++] = safe;
                            batchSizes[safe] = bytes;
                            continue;
                        }
                        ReleaseDiagnosticLocked(safe);
                    }
                    ReleaseDiagnosticLocked(captured);
                    batch.RemoveAt(index);
                }
            }

            foreach (var entry in pressure)
            {
                var kind = entry.Key;
                var dropped = entry.Value;
                var aggregate = BuildEnvelope("error_suppressed", new Dictionary<string, object?>
                {
                    ["fingerprint"] = SuppressionTracker.Fingerprint("queue_pressure", new Dictionary<string, object?> { ["name"] = kind }),
                    ["suppressed_count"] = dropped.Count,
                    ["first_seen"] = dropped.First.UtcDateTime.ToString("O"),
                    ["last_seen"] = dropped.Last.UtcDateTime.ToString("O"),
                    ["window_seconds"] = 60,
                    ["reason"] = "queue_pressure",
                    ["level"] = kind
                }, null);
                var safeAggregate = ProtectCandidate(aggregate);
                var preparedAggregate = safeAggregate == null ? null : BeforeSendProcessor.Apply(safeAggregate, _options.BeforeSend);
                preparedAggregate = preparedAggregate == null ? null : ProtectCandidate(preparedAggregate);
                if (preparedAggregate != null && BeforeSendProcessor.IsValid(preparedAggregate))
                {
                    AddPreparedAggregate(batch, batchSizes, preparedAggregate);
                }
            }

            foreach (var aggregate in _suppression.DrainAggregates(DateTimeOffset.UtcNow))
            {
                var aggregateEnvelope = BuildEnvelope("error_suppressed", new Dictionary<string, object?>
                {
                    ["fingerprint"] = aggregate.Fingerprint,
                    ["suppressed_count"] = aggregate.SuppressedCount,
                    ["first_seen"] = aggregate.FirstSeen.UtcDateTime.ToString("O"),
                    ["last_seen"] = aggregate.LastSeen.UtcDateTime.ToString("O"),
                    ["window_seconds"] = aggregate.WindowSeconds
                }, null);
                var safeAggregate = ProtectCandidate(aggregateEnvelope);
                var preparedAggregate = safeAggregate == null ? null : BeforeSendProcessor.Apply(safeAggregate, _options.BeforeSend);
                preparedAggregate = preparedAggregate == null ? null : ProtectCandidate(preparedAggregate);
                if (preparedAggregate != null && BeforeSendProcessor.IsValid(preparedAggregate))
                {
                    AddPreparedAggregate(batch, batchSizes, preparedAggregate);
                }
            }

            TakeNativePrefix(batch, batchSizes);
            if (batch.Count == 0)
            {
                lock (_sync) ReleaseInFlightLocked();
                return;
            }

            var response = await transport.SendAsync(new EventTransportRequest
            {
                ProjectToken = _options.ProjectToken,
                Events = batch
            }, cancellationToken).ConfigureAwait(false);
            batchToRestore = null;
            var acknowledgement = IngestionAcknowledgement.Decide(response.Body, batch.Count, transport is HttpEventTransport);
            var lostProof = response.StatusCode < 400 && _evidence.ObserveBody(response.Body, transport is HttpEventTransport, acknowledgement.Kind == IngestionAcknowledgementKind.Acknowledged);
            var compatible = new List<DebugBundleEventEnvelope>();
            if (lostProof && acknowledgement.Kind == IngestionAcknowledgementKind.Acknowledged)
            {
                foreach (var rejection in acknowledgement.TerminalErrors)
                {
                    if (rejection.Reason != "invalid_event") continue;
                    var reduced = DowngradeRejectedEvidence(batch[rejection.Index]);
                    if (reduced != null) compatible.Add(reduced);
                }
            }

            lock (_sync)
            {
                ReleaseInFlightLocked();
                if (_disposed) return;
                if (response.StatusCode == 429 || response.StatusCode >= 500)
                {
                    RestoreBatchLocked(batch, batchSizes);
                    _failures++;
                    Status = DebugBundleStatus.Degraded;
                    _retryUntil = DateTimeOffset.UtcNow + BoundedRetryAfter(response.RetryAfter, _failures);
                    ScheduleRetryLocked();
                    return;
                }

                if (response.StatusCode >= 400)
                {
                    foreach (var envelope in batch) ReleaseDiagnosticLocked(envelope);
                    _failures = 0;
                    Status = DebugBundleStatus.Healthy;
                    return;
                }

                if (acknowledgement.Kind == IngestionAcknowledgementKind.ProtocolFailure)
                {
                    RestoreBatchLocked(batch, batchSizes);
                    _failures++;
                    Status = DebugBundleStatus.Degraded;
                    _retryUntil = DateTimeOffset.UtcNow + BoundedRetryAfter(response.RetryAfter, _failures);
                    ScheduleRetryLocked();
                    return;
                }

                if (acknowledgement.Kind == IngestionAcknowledgementKind.Acknowledged)
                {
                    var retryableEvents = batch
                        .Where((_, index) => acknowledgement.RetryableIndices.Contains(index))
                        .ToList();
                    foreach (var reduced in compatible)
                    {
                        retryableEvents.Add(reduced); batchSizes[reduced] = EventBytes(reduced);
                    }
                    foreach (var envelope in batch)
                        if (!retryableEvents.Contains(envelope)) ReleaseDiagnosticLocked(envelope);
                    if (retryableEvents.Count > 0)
                    {
                        RestoreBatchLocked(retryableEvents, batchSizes);
                    }

                    if (acknowledgement.Accepted > 0)
                    {
                        LastEventAt = DateTimeOffset.UtcNow;
                    }

                    if (retryableEvents.Count > 0)
                    {
                        _failures++;
                        Status = DebugBundleStatus.Degraded;
                        _retryUntil = DateTimeOffset.UtcNow + BoundedRetryAfter(response.RetryAfter, _failures);
                        ScheduleRetryLocked();
                        return;
                    }

                    _failures = 0;
                    Status = acknowledgement.Accepted > 0
                        ? DebugBundleStatus.Healthy
                        : DebugBundleStatus.Disconnected;
                    _retryUntil = null;
                    return;
                }

                foreach (var envelope in batch) ReleaseDiagnosticLocked(envelope);
                _failures = 0;
                Status = DebugBundleStatus.Healthy;
                LastEventAt = DateTimeOffset.UtcNow;
                _retryUntil = null;
            }
        }
        catch
        {
            lock (_sync)
            {
                ReleaseInFlightLocked();
                if (_disposed) return;
                if (batchToRestore is { Count: > 0 })
                {
                    RestoreBatchLocked(batchToRestore, batchSizes);
                }

                _failures++;
                Status = DebugBundleStatus.Disconnected;
                _retryUntil = DateTimeOffset.UtcNow + DefaultBackoff(_failures);
                ScheduleRetryLocked();
            }
        }
    }

}
