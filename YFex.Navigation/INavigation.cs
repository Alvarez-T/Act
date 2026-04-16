using YFex.MVVM;

namespace YFex.NavigatR;

/// <summary>
/// Platform hook. Implemented once per platform adapter.
/// Each navigation context gets its own instance.
/// The core never calls platform APIs directly — all rendering goes through here.
/// </summary>
public interface INavigation
{
    /// <summary>
    /// The navigator that owns this navigation pane.
    /// Set by the factory during wiring.
    /// </summary>
    INavigator Navigator { get; set; }

    /// <summary>
    /// Called when navigating within the current context.
    /// The platform decides how to render (push frame, swap content, etc.).
    /// </summary>
    /// <param name="view">The resolved view object for the target navigable.</param>
    void PerformNavigation(object view);

    /// <summary>
    /// Called when switching to a different navigation context.
    /// The platform decides the visual transition.
    /// </summary>
    /// <param name="view">The resolved view object for the top of the target context.</param>
    void PerformContextSwitch(object view);

    /// <summary>
    /// Called when CanNavigate() returns false.
    /// The platform decides how to communicate the denial to the user.
    /// </summary>
    void OnNavigationDenied();
}