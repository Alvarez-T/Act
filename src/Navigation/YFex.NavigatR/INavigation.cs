namespace YFex.NavigatR;

/// <summary>
/// Platform-specific navigation hook. Implement this to connect NavigatR to your
/// platform's navigation host (WPF Frame, MAUI Shell, Avalonia, etc.).
/// <para>
/// Assign to <c>navigator.NavPane</c> after creating a context.
/// </para>
/// </summary>
public interface INavigation
{
    /// <summary>
    /// Called by the Navigator when a new screen should be shown.
    /// The <paramref name="view"/> is the resolved ViewModel instance.
    /// Map it to your platform's page/view and display it.
    /// </summary>
    void PerformNavigation(object view);

    /// <summary>
    /// Called by the Navigator when a navigation was denied in
    /// <see cref="INavigable.OnNavigation"/> via <see cref="NavigationContext.Deny"/>.
    /// </summary>
    void OnNavigationDenied();

    /// <summary>
    /// Called by the platform when the user becomes inactive (mouse idle, no touch,
    /// screen lock, etc.). The Navigator calls <see cref="INavigable.OnSuspend"/>
    /// on the active ViewModel without changing its state.
    /// Platform implementations should call <see cref="Navigator.NotifyInactive"/>
    /// when inactivity is detected.
    /// </summary>
    event Action? UserBecameInactive;

    /// <summary>
    /// Called by the platform when the user returns from inactivity.
    /// The Navigator calls <see cref="INavigable.OnActive"/>
    /// on the active ViewModel without changing its state.
    /// Platform implementations should call <see cref="Navigator.NotifyActive"/>
    /// when activity resumes.
    /// </summary>
    event Action? UserBecameActive;
}