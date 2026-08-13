using System.Diagnostics;
using YFex.System.Data;
using YFex.System.Platform;

namespace YFex.System.Windows;

public sealed class WindowsUserBehaviourTracker : IUserBehaviourTracker
{
    private long _clicks;
    private long _keystrokes;
    private long _scrolls;
    private long _resizes;
    private readonly Stopwatch _activeTimer = Stopwatch.StartNew();
    private readonly Stopwatch _idleTimer = new();

    public void RecordClick() => Interlocked.Increment(ref _clicks);
    public void RecordKeystroke() => Interlocked.Increment(ref _keystrokes);
    public void RecordScroll() => Interlocked.Increment(ref _scrolls);
    public void RecordWindowResize() => Interlocked.Increment(ref _resizes);

    public UserBehaviour GetSnapshot()
    {
        return new UserBehaviour(
            Interlocked.Read(ref _clicks),
            Interlocked.Read(ref _keystrokes),
            Interlocked.Read(ref _scrolls),
            (int)Interlocked.Read(ref _resizes),
            _activeTimer.Elapsed,
            _idleTimer.Elapsed);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _clicks, 0);
        Interlocked.Exchange(ref _keystrokes, 0);
        Interlocked.Exchange(ref _scrolls, 0);
        Interlocked.Exchange(ref _resizes, 0);
        _activeTimer.Restart();
        _idleTimer.Reset();
    }
}
