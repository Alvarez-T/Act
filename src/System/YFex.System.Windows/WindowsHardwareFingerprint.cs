using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using YFex.System.Platform;
using YFex.System.Telemetry;
using YFex.System.Unions;

namespace YFex.System.Windows;

public sealed class WindowsHardwareFingerprint : IHardwareFingerprintProvider
{
    private readonly string _salt;

    public WindowsHardwareFingerprint(IOptions<TelemetryOptions> options)
    {
        _salt = options.Value.TransportSaltKey;
    }

    public DataAvailability<string> GetDeviceId()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var machineGuid = key?.GetValue("MachineGuid") as string;

            if (machineGuid is null)
                return new Unsupported("Windows", "MachineGuid not found in registry");

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(machineGuid + _salt));
            return new Available<string>(
                Convert.ToHexString(hash).ToLowerInvariant(),
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new Unsupported("Windows", $"Failed to read MachineGuid: {ex.Message}");
        }
    }
}
