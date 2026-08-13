using System.Text.Json;

namespace YFex.Security.Events;

public sealed record TlsHandshakeEvent : ISecurityEvent
{
    public long Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public SecurityEventType EventType => SecurityEventType.TlsHandshake;
    public string Sni { get; init; } = "";
    public string CipherSuite { get; init; } = "";
    public string TlsVersion { get; init; } = "";
    public string Ja3Hash { get; init; } = "";
    public string Ja3sHash { get; init; } = "";
    public string CertSubject { get; init; } = "";
    public string CertIssuer { get; init; } = "";
    public DateTimeOffset? CertExpiry { get; init; }
    public string ToJson() => JsonSerializer.Serialize(this);
}
