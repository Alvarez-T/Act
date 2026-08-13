using YFex.System.Data;

namespace YFex.System.Platform;

public interface IUserBehaviourTracker
{
    void RecordClick();
    void RecordKeystroke();
    void RecordScroll();
    void RecordWindowResize();
    UserBehaviour GetSnapshot();
    void Reset();
}
