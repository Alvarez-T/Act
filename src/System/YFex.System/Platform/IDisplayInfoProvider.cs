using YFex.System.Data;
using YFex.System.Unions;

namespace YFex.System.Platform;

public interface IDisplayInfoProvider
{
    DataAvailability<DisplaySnapshot> Capture();
}
