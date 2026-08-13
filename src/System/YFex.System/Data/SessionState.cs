namespace YFex.System.Data;

public record IdleState(TimeSpan IdleDuration, bool IsIdle);

public record SessionEvent(string EventType, DateTimeOffset Timestamp);
