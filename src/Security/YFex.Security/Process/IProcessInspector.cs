namespace YFex.Security.Process;

public interface IProcessInspector
{
    IReadOnlyList<ProcessInfo> GetAllProcesses();
    ProcessInfo? GetProcessById(int pid);
    IReadOnlyList<string> GetProcessModules(int pid);
}
