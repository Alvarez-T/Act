namespace YFex.System.Windows.Security.Antivirus;

public record AntivirusProductInfo(
    string DisplayName,
    string InstanceGuid,
    string PathToSignedProductExe,
    bool IsEnabled,
    bool IsDefinitionsUpToDate);

public record DefenderStatus(
    IReadOnlyList<AntivirusProductInfo> InstalledProducts,
    bool AnyRealTimeProtectionEnabled);
