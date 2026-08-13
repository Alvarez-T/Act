using YFex.System.Data;
using YFex.System.Unions;

namespace YFex.System.Platform;

public interface IBrowserDataReader
{
    DataAvailability<IReadOnlyList<BrowserProfile>> DetectProfiles();
    DataAvailability<BrowserSnapshot> ReadProfile(BrowserProfile profile);
}
