namespace YFex.System.Windows.Security.Integrity;

public interface ISystemIntegrityChecker
{
    IntegrityReport GenerateReport();
    SecureBootStatus CheckSecureBoot();
    UacStatus CheckUac();
    FirewallStatus CheckFirewall();
    WindowsUpdateStatus CheckWindowsUpdate();
}
