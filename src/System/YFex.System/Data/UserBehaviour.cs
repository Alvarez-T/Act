namespace YFex.System.Data;

public record UserBehaviour(
    long ClickCount,
    long KeystrokeCount,
    long ScrollCount,
    int WindowResizeCount,
    TimeSpan ActiveDuration,
    TimeSpan IdleDuration
);
