using YFex.System.Unions;

namespace YFex.System.Platform;

public record ActiveWindowInfo(
    string ProcessName,
    int ProcessId,
    DataAvailability<string> WindowTitle
);

public interface IActiveWindowDetector
{
    DataAvailability<ActiveWindowInfo> GetActiveWindow();
}
