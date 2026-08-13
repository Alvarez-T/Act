namespace YFex.System.Windows.Security.FileSystem;

public record FileCreated(string Path, global::System.DateTimeOffset Timestamp);
public record FileModified(string Path, global::System.DateTimeOffset Timestamp);
public record FileDeleted(string Path, global::System.DateTimeOffset Timestamp);
public record FileRenamed(string OldPath, string NewPath, global::System.DateTimeOffset Timestamp);

public union FileChangeEvent(FileCreated, FileModified, FileDeleted, FileRenamed);
