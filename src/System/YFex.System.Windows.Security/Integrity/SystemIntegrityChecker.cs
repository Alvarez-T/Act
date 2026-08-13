using System.Management;
using MsReg = Microsoft.Win32;

namespace YFex.System.Windows.Security.Integrity;

public sealed class SystemIntegrityChecker : ISystemIntegrityChecker
{
    public IntegrityReport GenerateReport()
    {
        var secureBoot = CheckSecureBoot();
        var bitLocker = CheckBitLocker();
        var uac = CheckUac();
        var firewall = CheckFirewall();
        var windowsUpdate = CheckWindowsUpdate();

        int score = CalculateScore(secureBoot, bitLocker, uac, firewall, windowsUpdate);
        return new IntegrityReport(secureBoot, bitLocker, uac, firewall, windowsUpdate, score);
    }

    public SecureBootStatus CheckSecureBoot()
    {
        try
        {
            using var key = MsReg.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", false);
            var value = key?.GetValue("UEFISecureBootEnabled");
            return new SecureBootStatus(value is int v && v == 1);
        }
        catch
        {
            return new SecureBootStatus(false);
        }
    }

    private static BitLockerStatus CheckBitLocker()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2\Security\MicrosoftVolumeEncryption",
                "SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter = 'C:'");

            using var results = searcher.Get();
            foreach (var obj in results)
            {
                uint protectionStatus = Convert.ToUInt32(obj["ProtectionStatus"]);
                uint encryptionMethod = Convert.ToUInt32(obj["EncryptionMethod"]);

                string protectionStr = protectionStatus switch
                {
                    0 => "Off",
                    1 => "On",
                    2 => "Unknown",
                    _ => "Unknown"
                };

                string methodStr = encryptionMethod switch
                {
                    1 => "AES-128 Diffuser",
                    2 => "AES-256 Diffuser",
                    3 => "AES-128",
                    4 => "AES-256",
                    6 => "XTS-AES-128",
                    7 => "XTS-AES-256",
                    _ => "None"
                };

                obj.Dispose();
                return new BitLockerStatus(protectionStatus == 1, protectionStr, methodStr);
            }
        }
        catch { }

        return new BitLockerStatus(false, "Unavailable", "None");
    }

    public UacStatus CheckUac()
    {
        try
        {
            using var key = MsReg.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", false);

            if (key is null) return new UacStatus(false, 0);

            bool enabled = key.GetValue("EnableLUA") is int lua && lua == 1;
            int consentLevel = key.GetValue("ConsentPromptBehaviorAdmin") is int consent ? consent : 0;

            return new UacStatus(enabled, consentLevel);
        }
        catch
        {
            return new UacStatus(false, 0);
        }
    }

    public FirewallStatus CheckFirewall()
    {
        try
        {
            using var key = MsReg.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy", false);

            if (key is null) return new FirewallStatus(false, false, false);

            bool domain = ReadFirewallProfile(key, "DomainProfile");
            bool priv = ReadFirewallProfile(key, "StandardProfile");
            bool pub = ReadFirewallProfile(key, "PublicProfile");

            return new FirewallStatus(domain, priv, pub);
        }
        catch
        {
            return new FirewallStatus(false, false, false);
        }
    }

    public WindowsUpdateStatus CheckWindowsUpdate()
    {
        try
        {
            using var key = MsReg.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install", false);

            global::System.DateTimeOffset? lastInstall = null;
            if (key?.GetValue("LastSuccessTime") is string timeStr
                && DateTime.TryParse(timeStr, out var dt))
            {
                lastInstall = new DateTimeOffset(dt, TimeSpan.Zero);
            }

            using var auKey = MsReg.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update", false);
            bool autoUpdate = auKey?.GetValue("AUOptions") is int au && au >= 3;

            return new WindowsUpdateStatus(lastInstall, autoUpdate);
        }
        catch
        {
            return new WindowsUpdateStatus(null, false);
        }
    }

    private static bool ReadFirewallProfile(MsReg.RegistryKey parentKey, string profileName)
    {
        try
        {
            using var profileKey = parentKey.OpenSubKey(profileName, false);
            return profileKey?.GetValue("EnableFirewall") is int fw && fw == 1;
        }
        catch
        {
            return false;
        }
    }

    private static int CalculateScore(
        SecureBootStatus secureBoot,
        BitLockerStatus bitLocker,
        UacStatus uac,
        FirewallStatus firewall,
        WindowsUpdateStatus windowsUpdate)
    {
        int score = 0;

        if (secureBoot.IsEnabled) score += 15;
        if (bitLocker.IsEnabled) score += 20;
        if (uac.IsEnabled) score += 15;
        if (uac.ConsentPromptLevel >= 2) score += 5;
        if (firewall.DomainEnabled) score += 5;
        if (firewall.PrivateEnabled) score += 5;
        if (firewall.PublicEnabled) score += 10;
        if (windowsUpdate.AutoUpdateEnabled) score += 10;

        if (windowsUpdate.LastInstallDate is { } lastDate)
        {
            var daysSinceUpdate = (DateTimeOffset.UtcNow - lastDate).TotalDays;
            score += daysSinceUpdate switch
            {
                <= 7 => 15,
                <= 30 => 10,
                <= 90 => 5,
                _ => 0
            };
        }

        return Math.Min(score, 100);
    }
}
