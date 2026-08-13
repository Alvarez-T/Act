using YFex.System.Data;

namespace YFex.System.Platform;

public sealed class NullUserBehaviourTracker : IUserBehaviourTracker
{
    public void RecordClick() { }
    public void RecordKeystroke() { }
    public void RecordScroll() { }
    public void RecordWindowResize() { }
    public UserBehaviour GetSnapshot() => new(0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero);
    public void Reset() { }
}
