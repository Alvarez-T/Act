using YFex.Security.Process;
using SysDiag = global::System.Diagnostics;

namespace YFex.Windows.Security.Process;

public sealed class ProcessInspector : IProcessInspector
{
    public IReadOnlyList<ProcessInfo> GetAllProcesses()
    {
        var processes = SysDiag.Process.GetProcesses();
        var result = new List<ProcessInfo>(processes.Length);

        foreach (var p in processes)
        {
            try
            {
                result.Add(BuildProcessInfo(p));
            }
            catch { }
            finally { p.Dispose(); }
        }

        return result;
    }

    public ProcessInfo? GetProcessById(int pid)
    {
        try
        {
            using var p = SysDiag.Process.GetProcessById(pid);
            return BuildProcessInfo(p);
        }
        catch { return null; }
    }

    public IReadOnlyList<string> GetProcessModules(int pid)
    {
        try
        {
            using var p = SysDiag.Process.GetProcessById(pid);
            var modules = new List<string>();
            foreach (SysDiag.ProcessModule module in p.Modules)
            {
                try { modules.Add(module.FileName ?? module.ModuleName); }
                catch { }
            }
            return modules;
        }
        catch { return []; }
    }

    private static ProcessInfo BuildProcessInfo(SysDiag.Process p)
    {
        string? path = null;
        try { path = p.MainModule?.FileName; }
        catch { }

        DateTimeOffset? startTime = null;
        try { startTime = p.StartTime.ToUniversalTime(); }
        catch { }

        long workingSet = 0;
        try { workingSet = p.WorkingSet64; }
        catch { }

        var modules = new List<string>();
        try
        {
            foreach (SysDiag.ProcessModule module in p.Modules)
            {
                try { modules.Add(module.FileName ?? module.ModuleName); }
                catch { }
            }
        }
        catch { }

        return new ProcessInfo(p.Id, p.ProcessName, path, startTime, workingSet, modules);
    }
}
