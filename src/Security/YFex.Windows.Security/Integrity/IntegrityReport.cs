namespace YFex.Windows.Security.Integrity;

public record SecureBootStatus(bool IsEnabled);
public record BitLockerStatus(bool IsEnabled, string ProtectionStatus, string EncryptionMethod);
public record UacStatus(bool IsEnabled, int ConsentPromptLevel);
public record FirewallStatus(bool DomainEnabled, bool PrivateEnabled, bool PublicEnabled);
public record WindowsUpdateStatus(DateTimeOffset? LastInstallDate, bool AutoUpdateEnabled);

public record IntegrityReport(
    SecureBootStatus SecureBoot,
    BitLockerStatus BitLocker,
    UacStatus Uac,
    FirewallStatus Firewall,
    WindowsUpdateStatus WindowsUpdate,
    int OverallScore);
