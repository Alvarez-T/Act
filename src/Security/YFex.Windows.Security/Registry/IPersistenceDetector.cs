namespace YFex.Windows.Security.Registry;

public interface IPersistenceDetector
{
    IReadOnlyList<AutostartEntry> EnumerateAll();
    IReadOnlyList<AutostartEntry> ScanForNew(IReadOnlyList<AutostartEntry> baseline);
}
