using YFex.State.Mvvm;
using YFex.UI.Abstractions;

namespace YFex.Mvvm;

/// <summary>
/// Base ViewModel. Provides UI service dependencies (Notification, Dialog, Toast)
/// and inherits the full YFex.State reactive stack via <see cref="MvvmStateObject"/>.
/// <para>
/// Extend via DI using the parameterized constructor. The parameterless constructor
/// is provided for test subclasses that do not require service injection.
/// </para>
/// </summary>
public abstract class ViewModel : MvvmStateObject
{
    public INotification Notification { get; }
    public IDialog       Dialog       { get; }
    public IToast        Toast        { get; }

    /// <summary>DI constructor — used by the service container.</summary>
    protected ViewModel(INotification notification, IDialog dialog, IToast toast)
    {
        Notification = notification;
        Dialog       = dialog;
        Toast        = toast;
    }

    /// <summary>
    /// Parameterless constructor for test subclasses that do not use DI.
    /// Service properties will be <see langword="null"/> — do not call them in production code.
    /// </summary>
    protected ViewModel() : this(null!, null!, null!) { }
}
