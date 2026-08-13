namespace YFex.System.Windows.Security.Registry;

public record ValueChanged(string KeyPath, string ValueName, string? OldValue, string? NewValue, global::System.DateTimeOffset Timestamp);
public record KeyCreated(string KeyPath, global::System.DateTimeOffset Timestamp);
public record KeyDeleted(string KeyPath, global::System.DateTimeOffset Timestamp);

public union RegistryChangeEvent(ValueChanged, KeyCreated, KeyDeleted);
