using YFex.System.Unions;

namespace YFex.System.Platform;

public interface IHardwareFingerprintProvider
{
    DataAvailability<string> GetDeviceId();
}
