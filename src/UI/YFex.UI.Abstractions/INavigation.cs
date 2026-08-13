namespace YFex.UI.Abstractions;

/// <summary>
/// Platform-specific navigation hook. Implement this to connect a navigation engine
/// (e.g. NavigatR) to your platform's navigation host (WPF Frame, MAUI Shell, Avalonia, etc.).
/// <para>
/// Assign the implementation to the navigator's nav pane after creating a context.
/// </para>
/// </summary>
public interface INavigation
{
    /// <summary>
    /// Called by the navigator when a new screen should be shown.
    /// The <paramref name="view"/> is the resolved ViewModel instance.
    /// Map it to your platform's page/view and display it.
    /// </summary>
    void PerformNavigation(object view);

    /// <summary>
    /// Called by the navigator when a navigation was denied by the target
    /// ViewModel during its <c>OnNavigation</c> guard.
    /// </summary>
    void OnNavigationDenied();

    /// <summary>
    /// Raised by the platform when the user becomes inactive (mouse idle, no touch,
    /// screen lock, etc.). The navigator calls <c>OnSuspend</c> on the active ViewModel
    /// without changing its state. Platform implementations should raise this when
    /// inactivity is detected so the navigator can call <c>NotifyInactive</c>.
    /// </summary>
    event Action? UserBecameInactive;

    /// <summary>
    /// Raised by the platform when the user returns from inactivity.
    /// The navigator calls <c>OnActive</c> on the active ViewModel without changing
    /// its state. Platform implementations should raise this when activity resumes
    /// so the navigator can call <c>NotifyActive</c>.
    /// </summary>
    event Action? UserBecameActive;
}
