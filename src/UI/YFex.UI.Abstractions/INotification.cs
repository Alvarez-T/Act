// ── Notification options ──────────────────────────────────────────────────────

using YFex.UI.Abstractions;

public interface INotificationOptions;

// ── Notification handle ───────────────────────────────────────────────────────

/// <summary>
/// Represents an active notification with no result.
/// Can be dismissed programmatically or awaited until the user dismisses or it times out.
/// </summary>
public interface INotificationHandle
{
    void Dismiss();
    Task UntilClosedAsync();
}

/// <summary>
/// Represents an active notification that produces a result.
/// </summary>
public interface INotificationHandle<TResult> : INotificationHandle
{
    new Task<TResult> UntilClosedAsync();
}

public interface INotificationBuilder
{
    // Without timeout — no Ignored in union
    INotificationHandle<YesNo> YesNo();
    INotificationHandle<YesNoCancel> YesNoCancel();
    INotificationHandle<OkCancel> OkCancel();
    INotificationHandle<TResult> WithChoices<TResult>(IReadOnlyList<DialogOption<TResult>> options);

    // Timeout variant — Ignored added to union
    INotificationBuilderWithTimeout WithTimeout(TimeSpan duration);
}

public interface INotificationBuilderWithTimeout
{
    INotificationHandle<YesNoIgnored> YesNo();
    INotificationHandle<YesNoCancelIgnored> YesNoCancel();
    INotificationHandle<OkCancelIgnored> OkCancel();
    INotificationHandle<TResult> WithChoices<TResult>(IReadOnlyList<DialogOption<TResult>> options);
}


public interface INotification
{
    /// <summary>Fire and forget. No result expected.</summary>
    void Notify<TView>(INotificationOptions? options = null);

    /// <summary>Returns a builder to configure result type and timeout.</summary>
    INotificationBuilder Notify<TView, TResult>(INotificationOptions? options = null);
}

// ── Convenience extensions ────────────────────────────────────────────────────

public static class NotificationExtensions
{
    // Result bool shorthands — on the result types
    public static bool IsConfirmed(this YesNo result) => result is YesNo.Yes;
    public static bool IsConfirmed(this YesNoIgnored result) => result is YesNoIgnored.Yes;
    public static bool IsIgnored(this YesNoIgnored result) => result is YesNoIgnored.Ignored;
    public static bool IsConfirmed(this YesNoCancel result) => result is YesNoCancel.Yes;
    public static bool IsCancelled(this YesNoCancel result) => result is YesNoCancel.Cancel;
    public static bool IsConfirmed(this YesNoCancelIgnored result) => result is YesNoCancelIgnored.Yes;
    public static bool IsCancelled(this YesNoCancelIgnored result) => result is YesNoCancelIgnored.Cancel;
    public static bool IsIgnored(this YesNoCancelIgnored result) => result is YesNoCancelIgnored.Ignored;
    public static bool IsProceeded(this OkCancel result) => result is OkCancel.Ok;
    public static bool IsProceeded(this OkCancelIgnored result) => result is OkCancelIgnored.Ok;
    public static bool IsIgnored(this OkCancelIgnored result) => result is OkCancelIgnored.Ignored;
}