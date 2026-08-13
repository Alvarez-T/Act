using YFex.State.Mvvm;
using YFex.UI.Public;

namespace YFex.Mvvm;

/// <summary>
/// Base ViewModel. Exposes the shared UI services (Toast, Notification, MessageBox, Dialog)
/// and inherits the full YFex.State reactive stack via <see cref="MvvmStateObject"/>.
/// <para>
/// Services are resolved from their ambient locators in the constructor, so subclasses
/// never declare or forward them — a subclass constructor takes only its own dependencies.
/// A framework project (e.g. YFex.Avalonia) must register the concrete services during
/// startup before any ViewModel is constructed.
/// </para>
/// </summary>
public abstract class ViewModel : MvvmStateObject
{
    public IToast        Toast        { get; }
    public INotification Notification { get; }
    public IMessageBox   MessageBox   { get; }
    public IDialog       Dialog       { get; }

    protected ViewModel()
    {
        Toast        = ToastLocator.Current;
        Notification = NotificationLocator.Current;
        MessageBox   = MessageBoxLocator.Current;
        Dialog       = DialogLocator.Current;
    }
}
