namespace YFex.System.Windows.Security.Antivirus;

public interface IAmsiScanner
{
    bool IsAvailable { get; }
    AmsiScanResult ScanBuffer(byte[] buffer, string contentName = "");
    AmsiScanResult ScanFile(string filePath);
}
