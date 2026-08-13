using YFex.System.Data;
using YFex.System.Unions;

namespace YFex.System.Platform;

public interface IIdleDetector
{
    DataAvailability<IdleState> GetCurrentIdleState();
}
