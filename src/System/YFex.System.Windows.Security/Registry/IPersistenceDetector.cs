namespace YFex.System.Windows.Security.Registry;

public interface IPersistenceDetector
{
    IReadOnlyList<AutostartEntry> EnumerateAll();
    IReadOnlyList<AutostartEntry> ScanForNew(IReadOnlyList<AutostartEntry> baseline);
}
