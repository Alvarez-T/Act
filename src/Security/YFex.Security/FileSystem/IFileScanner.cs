namespace YFex.Security.FileSystem;

public interface IFileScanner
{
    string HashFile(string filePath);
    SignatureResult VerifySignature(string filePath);
    FileScanResult ScanFile(string filePath);
    IReadOnlyList<FileScanResult> ScanDirectory(string directoryPath, string searchPattern = "*.exe");
}
