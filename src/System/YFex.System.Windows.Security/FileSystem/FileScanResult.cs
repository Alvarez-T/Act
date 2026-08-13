namespace YFex.System.Windows.Security.FileSystem;

public record Safe;
public record LowThreat(string Reason);
public record MediumThreat(string Reason);
public record HighThreat(string Reason);
public record CriticalThreat(string Reason);

public union ThreatLevel(Safe, LowThreat, MediumThreat, HighThreat, CriticalThreat);

public record HashResult(string Algorithm, string Hash);

public record SignatureResult(
    bool IsSigned,
    string? Subject,
    string? Issuer,
    bool IsValid,
    global::System.DateTimeOffset? Expiry);

public record FileScanResult(
    string FilePath,
    HashResult Hash,
    SignatureResult Signature,
    ThreatLevel Threat);
