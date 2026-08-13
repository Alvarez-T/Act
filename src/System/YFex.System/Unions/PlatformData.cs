namespace YFex.System.Unions;

public record DataField(string Name, object? Value, string Category);

public record CollectionError(string Subsystem, Exception Exception);

public record PartialResult(DataField[] Collected, CollectionError[] Errors);

public union SnapshotResult(PartialResult, Denied, Unsupported, CollectionError);
