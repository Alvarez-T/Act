using System.Runtime.InteropServices;
using YFex.System.Internal;
using YFex.System.Windows.Security.Interop;

namespace YFex.System.Windows.Security.Input;

public sealed class SystemInputMonitor : ISystemInputMonitor
{
    private readonly ObservableSource<InputEvent> _events = new();
    private Thread? _hookThread;
    private nint _keyboardHook;
    private nint _mouseHook;
    private uint _hookThreadId;
    private volatile bool _running;

    private HookInterop.HookProc? _keyboardProc;
    private HookInterop.HookProc? _mouseProc;

    public IObservable<InputEvent> Events => _events;
    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;
        _running = true;

        _hookThread = new Thread(HookThreadProc)
        {
            IsBackground = true,
            Name = "YFex.Security.InputHooks"
        };
        _hookThread.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        if (_hookThreadId != 0)
            HookInterop.PostThreadMessageW(_hookThreadId, HookInterop.WM_QUIT, (nint)0, (nint)0);

        _hookThread?.Join(TimeSpan.FromSeconds(3));
        _hookThread = null;
    }

    private void HookThreadProc()
    {
        _hookThreadId = HookInterop.GetCurrentThreadId();
        var moduleHandle = HookInterop.GetModuleHandleW(null);

        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;

        _keyboardHook = HookInterop.SetWindowsHookExW(
            HookInterop.WH_KEYBOARD_LL, _keyboardProc, moduleHandle, 0);
        _mouseHook = HookInterop.SetWindowsHookExW(
            HookInterop.WH_MOUSE_LL, _mouseProc, moduleHandle, 0);

        while (HookInterop.GetMessageW(out var msg, nint.Zero, 0, 0) > 0)
        {
            HookInterop.TranslateMessage(ref msg);
            HookInterop.DispatchMessageW(ref msg);
        }

        if (_keyboardHook != nint.Zero)
        {
            HookInterop.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = nint.Zero;
        }
        if (_mouseHook != nint.Zero)
        {
            HookInterop.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = nint.Zero;
        }

        _keyboardProc = null;
        _mouseProc = null;
    }

    private nint KeyboardHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= HookInterop.HC_ACTION)
        {
            var kb = Marshal.PtrToStructure<HookInterop.KBDLLHOOKSTRUCT>(lParam);
            var action = (int)wParam switch
            {
                HookInterop.WM_KEYDOWN => (KeyAction?)KeyAction.KeyDown,
                HookInterop.WM_KEYUP => KeyAction.KeyUp,
                HookInterop.WM_SYSKEYDOWN => KeyAction.SystemKeyDown,
                HookInterop.WM_SYSKEYUP => KeyAction.SystemKeyUp,
                _ => null
            };

            if (action is KeyAction a)
            {
                InputEvent evt = new KeyboardEvent(kb.vkCode, kb.scanCode, a, DateTimeOffset.UtcNow);
                _events.Emit(evt);
            }
        }
        return HookInterop.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= HookInterop.HC_ACTION)
        {
            var ms = Marshal.PtrToStructure<HookInterop.MSLLHOOKSTRUCT>(lParam);
            int scrollDelta = 0;
            var action = (int)wParam switch
            {
                HookInterop.WM_MOUSEMOVE => (MouseAction?)MouseAction.Move,
                HookInterop.WM_LBUTTONDOWN => MouseAction.LeftDown,
                HookInterop.WM_LBUTTONUP => MouseAction.LeftUp,
                HookInterop.WM_RBUTTONDOWN => MouseAction.RightDown,
                HookInterop.WM_RBUTTONUP => MouseAction.RightUp,
                HookInterop.WM_MBUTTONDOWN => MouseAction.MiddleDown,
                HookInterop.WM_MBUTTONUP => MouseAction.MiddleUp,
                HookInterop.WM_MOUSEWHEEL => MouseAction.Scroll,
                _ => null
            };

            if (action == MouseAction.Scroll)
                scrollDelta = (short)(ms.mouseData >> 16);

            if (action is MouseAction a)
            {
                InputEvent evt = new MouseEvent(ms.pt.X, ms.pt.Y, a, scrollDelta, DateTimeOffset.UtcNow);
                _events.Emit(evt);
            }
        }
        return HookInterop.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
        _events.Complete();
    }
}
