using YFex.System.Platform;
using YFex.Windows.Security.Input;

namespace YFex.Windows.Security.Bridge;

public sealed class InputBehaviourBridge : global::System.IDisposable
{
    private readonly ISystemInputMonitor _monitor;
    private readonly IUserBehaviourTracker _tracker;
    private global::System.IDisposable? _subscription;

    public InputBehaviourBridge(ISystemInputMonitor monitor, IUserBehaviourTracker tracker)
    {
        _monitor = monitor;
        _tracker = tracker;
    }

    public void Start()
    {
        _subscription = _monitor.Events.Subscribe(new InputObserver(_tracker));
        if (!_monitor.IsRunning) _monitor.Start();
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private sealed class InputObserver(IUserBehaviourTracker tracker) : global::System.IObserver<InputEvent>
    {
        public void OnNext(InputEvent value)
        {
            switch (value)
            {
                case KeyboardEvent kb:
                    if (kb.Action == KeyAction.KeyDown)
                        tracker.RecordKeystroke();
                    break;
                case MouseEvent mouse:
                    switch (mouse.Action)
                    {
                        case MouseAction.LeftDown or MouseAction.RightDown or MouseAction.MiddleDown:
                            tracker.RecordClick();
                            break;
                        case MouseAction.Scroll:
                            tracker.RecordScroll();
                            break;
                    }
                    break;
            }
        }

        public void OnError(global::System.Exception error) { }
        public void OnCompleted() { }
    }
}
