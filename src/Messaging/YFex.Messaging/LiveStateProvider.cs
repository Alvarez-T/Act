using YFex.Messaging.Internal;

namespace YFex.Messaging;

/// <summary>
/// Static locator for the active <see cref="ILiveStateFactory"/>.
/// Defaults to <see cref="DefaultLiveStateFactory"/> (task-based, no caching).
/// Call <see cref="Configure"/> to replace with a Fusion-backed factory.
/// </summary>
public static class LiveStateProvider
{
    private static ILiveStateFactory _factory = new DefaultLiveStateFactory();

    /// <summary>Current factory. Never null — falls back to the default implementation.</summary>
    public static ILiveStateFactory Current => _factory;

    /// <summary>
    /// Replaces the factory. Called by <c>AddYFexFusion()</c> to install the
    /// Stl.Fusion-backed implementation.
    /// </summary>
    public static void Configure(ILiveStateFactory factory) => _factory = factory;
}
