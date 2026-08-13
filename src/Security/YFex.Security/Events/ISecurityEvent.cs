namespace YFex.Security.Events;

public interface ISecurityEvent
{
    long Id { get; }
    DateTimeOffset Timestamp { get; }
    int ProcessId { get; }
    string ProcessName { get; }
    SecurityEventType EventType { get; }
    string ToJson();
}
