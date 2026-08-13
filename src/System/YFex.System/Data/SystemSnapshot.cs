namespace YFex.System.Data;

public record SystemSnapshot
{
    public required string OsDescription { get; init; }
    public required string OsArchitecture { get; init; }
    public required bool Is64BitOs { get; init; }
    public required string RuntimeVersion { get; init; }
    public required int ProcessorCount { get; init; }
    public required long WorkingSetBytes { get; init; }
    public required long GcTotalMemory { get; init; }
    public required string MachineName { get; init; }
    public required string UserName { get; init; }
    public required string UserDomainName { get; init; }
    public required bool IsElevated { get; init; }
    public required string AppVersion { get; init; }
    public required string AppPath { get; init; }
    public required long AppUptimeMs { get; init; }
}
