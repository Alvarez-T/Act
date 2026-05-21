namespace YFex.UI.Abstractions;

public interface IToast
{
    void Show(string message, MessageIcon icon, TimeSpan duration);
}

public static class ToastExtensions
{
    public static void ShowSuccess(this IToast toast, string message, TimeSpan? duration = null)
        => toast.Show(message, MessageIcon.Success, duration ?? TimeSpan.FromSeconds(3));

    public static void ShowInfo(this IToast toast, string message, TimeSpan? duration = null)
        => toast.Show(message, MessageIcon.Info, duration ?? TimeSpan.FromSeconds(3));

    public static void ShowWarning(this IToast toast, string message, TimeSpan? duration = null)
        => toast.Show(message, MessageIcon.Warning, duration ?? TimeSpan.FromSeconds(5));

    public static void ShowError(this IToast toast, string message, TimeSpan? duration = null)
        => toast.Show(message, MessageIcon.Error, duration ?? TimeSpan.FromSeconds(5));
}