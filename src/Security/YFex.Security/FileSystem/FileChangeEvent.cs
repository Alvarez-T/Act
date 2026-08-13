namespace YFex.Security.FileSystem;

public record FileCreated(string Path, DateTimeOffset Timestamp);
public record FileModified(string Path, DateTimeOffset Timestamp);
public record FileDeleted(string Path, DateTimeOffset Timestamp);
public record FileRenamed(string OldPath, string NewPath, DateTimeOffset Timestamp);

public union FileChangeEvent(FileCreated, FileModified, FileDeleted, FileRenamed);
