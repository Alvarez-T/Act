using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using YFex.Security.FileSystem;
using YFex.Windows.Security.Antivirus;

namespace YFex.Windows.Security.FileSystem;

public sealed class FileScanner(IAmsiScanner amsi) : IFileScanner
{
    public string HashFile(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }

    public SignatureResult VerifySignature(string filePath)
    {
        try
        {
#pragma warning disable SYSLIB0057
            var rawCert = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            using var cert = new X509Certificate2(rawCert);
            return new SignatureResult(
                true,
                cert.Subject,
                cert.Issuer,
                cert.Verify(),
                cert.NotAfter);
        }
        catch
        {
            return new SignatureResult(false, null, null, false, null);
        }
    }

    public FileScanResult ScanFile(string filePath)
    {
        var hash = new HashResult("SHA256", HashFile(filePath));
        var sig = VerifySignature(filePath);
        var amsiResult = amsi.ScanFile(filePath);
        var threat = DetermineThreat(sig, amsiResult, filePath);
        return new FileScanResult(filePath, hash, sig, threat);
    }

    public IReadOnlyList<FileScanResult> ScanDirectory(string directoryPath, string searchPattern = "*.exe")
    {
        if (!Directory.Exists(directoryPath)) return [];

        var files = Directory.GetFiles(directoryPath, searchPattern, SearchOption.AllDirectories);
        var results = new List<FileScanResult>(files.Length);

        foreach (var file in files)
        {
            try { results.Add(ScanFile(file)); }
            catch { }
        }

        return results;
    }

    private static ThreatLevel DetermineThreat(SignatureResult sig, AmsiScanResult amsi, string path)
    {
        if (amsi is Detected) return new CriticalThreat("AMSI detected malware");
        if (amsi is BlockedByAdmin) return new HighThreat("Blocked by security policy");

        if (!sig.IsSigned)
        {
            var lower = path.ToLowerInvariant();
            if (lower.Contains("\\temp\\") || lower.Contains("\\tmp\\"))
                return new MediumThreat("Unsigned executable in temp directory");
            if (lower.Contains("\\appdata\\"))
                return new LowThreat("Unsigned executable in AppData");
        }

        if (sig.IsSigned && !sig.IsValid)
            return new MediumThreat("Invalid or expired digital signature");

        return new Safe();
    }
}
