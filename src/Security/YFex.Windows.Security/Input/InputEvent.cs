namespace YFex.Windows.Security.Input;

public enum KeyAction { KeyDown, KeyUp, SystemKeyDown, SystemKeyUp }

public enum MouseAction { Move, LeftDown, LeftUp, RightDown, RightUp, MiddleDown, MiddleUp, Scroll }

public record KeyboardEvent(uint VkCode, uint ScanCode, KeyAction Action, DateTimeOffset Timestamp);

public record MouseEvent(int X, int Y, MouseAction Action, int ScrollDelta, DateTimeOffset Timestamp);

public union InputEvent(KeyboardEvent, MouseEvent);
