using System.Runtime.CompilerServices;
using System.Text.Json;
using DebugBundle.Transport;

namespace DebugBundle.AspNetCore.Relay;

// Proof follows this actual forward path. Reconfiguration cannot reuse evidence
// discovered for another endpoint, credential, forwarder or complete-path pin.
internal sealed class RelayEvidenceState
{
    private static readonly ConditionalWeakTable<DebugBundleRelayOptions, RelayEvidenceState> States = new();
    private readonly object _sync = new();
    private string _identity = "";
    private IEventTransport? _transport;
    private EvidenceDeliveryState _state = new(null);
    internal static EvidenceDeliveryState For(DebugBundleOptions options, DebugBundleRelayOptions relay, string token)
    {
        var owner = States.GetValue(relay, _ => new RelayEvidenceState());
        var identity = options.Endpoint.AbsoluteUri + "\n" + token + "\n" + JsonSerializer.Serialize(EvidenceDeliveryState.Normalize(options.EvidenceCapabilities));
        lock (owner._sync)
        {
            if (owner._identity != identity || !ReferenceEquals(owner._transport, relay.ForwardTransport))
            { owner._identity = identity; owner._transport = relay.ForwardTransport; owner._state = new EvidenceDeliveryState(options.EvidenceCapabilities); }
            return owner._state;
        }
    }
}
