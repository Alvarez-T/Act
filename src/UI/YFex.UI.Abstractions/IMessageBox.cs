namespace YFex.UI.Abstractions;

public interface IMessageBox
{
    void ShowMessage(string title, string message, MessageIcon icon = MessageIcon.None);
    IMessageBoxAskBuilder Ask(string title, string question, MessageIcon icon = MessageIcon.Question);
}

public interface IMessageBoxAskBuilder
{
    YesNo YesNo();
    YesNoCancel YesNoCancel();
    OkCancel OkCancel();
    TResult WithChoices<TResult>(IReadOnlyList<DialogOption<TResult>> options);
}

public static class MessageBoxExtensions
{
    // ShowMessage icon shorthands
    public static void ShowInfo(this IMessageBox mb, string title, string message)
        => mb.ShowMessage(title, message, MessageIcon.Info);

    public static void ShowWarning(this IMessageBox mb, string title, string message)
        => mb.ShowMessage(title, message, MessageIcon.Warning);

    public static void ShowError(this IMessageBox mb, string title, string message)
        => mb.ShowMessage(title, message, MessageIcon.Error);

    public static void ShowSuccess(this IMessageBox mb, string title, string message)
        => mb.ShowMessage(title, message, MessageIcon.Success);

    // Result bool shorthands — on the result types, not on IMessageBox
    public static bool IsConfirmed(this YesNo result) => result is YesNo.Yes;
    public static bool IsConfirmed(this YesNoCancel result) => result is YesNoCancel.Yes;
    public static bool IsProceeded(this OkCancel result) => result is OkCancel.Ok;
    public static bool IsCancelled(this YesNoCancel result) => result is YesNoCancel.Cancel;
}


/// <summary>A single button in a custom dialog.</summary>
public readonly record struct DialogOption<TResult>(
    string Label,
    TResult Result,
    bool IsDefault = false,
    bool IsCancel = false
);
