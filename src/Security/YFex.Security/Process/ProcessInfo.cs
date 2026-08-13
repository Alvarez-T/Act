namespace YFex.Security.Process;

public record ProcessInfo(
    int Pid,
    string Name,
    string? FilePath,
    DateTimeOffset? StartTime,
    long WorkingSetBytes,
    IReadOnlyList<string> LoadedModules
);
