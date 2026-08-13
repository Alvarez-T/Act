namespace YFex.Windows.Security.Registry;

public record ValueChanged(string KeyPath, string ValueName, string? OldValue, string? NewValue, DateTimeOffset Timestamp);
public record KeyCreated(string KeyPath, DateTimeOffset Timestamp);
public record KeyDeleted(string KeyPath, DateTimeOffset Timestamp);

public union RegistryChangeEvent(ValueChanged, KeyCreated, KeyDeleted);
