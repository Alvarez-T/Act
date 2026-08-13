namespace YFex.System.Windows.Security.Process;

public record ProcessCreated(int Pid, string Name, string? FilePath, DateTimeOffset Timestamp);

public record ProcessTerminated(int Pid, DateTimeOffset Timestamp);

public union ProcessEvent(ProcessCreated, ProcessTerminated);
