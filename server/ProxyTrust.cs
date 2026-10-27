using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace Chispa.Deployment;

// Which peers may speak for someone else.
//
// The production shape is: public :24154 (TLS, terminated by the tunnel client on this machine)
// -> frpc -> http://127.0.0.1:5115. So every request reaches Kestrel from loopback, carrying
// X-Forwarded-For / X-Forwarded-Proto that frpc wrote. The tunnel client does NOT strip headers a
// client sent: a request that arrived with `X-Forwarded-For: 6.6.6.6` reaches the service as
// `X-Forwarded-For: 6.6.6.6, <real client>` (verified against the live tunnel), and X-Real-IP and
// Forwarded arrive untouched. Trusting the first entry, or X-Real-IP, or RFC 7239 `Forwarded`,
// therefore means letting anyone choose the address this service rate-limits and logs.
//
// What is trusted is stated, not inferred: CHISPA_TRUSTED_PROXIES lists the peers that may set
// these headers, nothing else is accepted, and the framework's default trust in loopback is
// cleared so that "no configuration" really means "no header is believed". With one trusted hop
// (ForwardLimit = 1) only the right-most entry - the one the proxy appended - is used, which is
// the only entry the client could not have written.
sealed class ProxyTrust
{
    public sealed record Entry(string Raw, IPAddress? Address, IPNetwork? Network);

    public IReadOnlyList<Entry> Entries { get; }

    private ProxyTrust(IReadOnlyList<Entry> entries) => Entries = entries;

    public bool Enabled => Entries.Count > 0;

    public static ProxyTrust Parse(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return new ProxyTrust([]);
        var entries = new List<Entry>();
        foreach (var raw in configured.Split([',', ';', ' ', '\t', '\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(raw, out var address))
                entries.Add(new Entry(raw, address, null));
            else if (IPNetwork.TryParse(raw, out var network))
                entries.Add(new Entry(raw, null, network));
            else
                throw new InvalidOperationException(
                    $"CHISPA_TRUSTED_PROXIES contains '{raw}', which is neither an IP address nor a CIDR range.");
        }
        return new ProxyTrust(entries);
    }

    public void Apply(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // The framework trusts loopback out of the box. Clearing the lists is what makes the
        // configured list the whole truth instead of a hint.
        options.KnownProxies.Clear();
        // `KnownNetworks` is the obsolete spelling of the same list; the new property is what a
        // loopback default would be cleared from.
        options.KnownIPNetworks.Clear();
        foreach (var entry in Entries)
        {
            if (entry.Address is not null) options.KnownProxies.Add(entry.Address);
            if (entry.Network is { } network) options.KnownIPNetworks.Add(network);
        }
        // One hop. Anything further left in the chain was written by the client and is ignored.
        options.ForwardLimit = 1;
    }

    public string Describe() => Enabled
        ? "trusted proxies: " + string.Join(", ", Entries.Select(entry => entry.Raw))
        : "trusted proxies: none (X-Forwarded-* are ignored)";
}
