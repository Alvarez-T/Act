namespace YFex.Cqrs;

/// <summary>
/// Ambient accessor for the registered <see cref="IDispatcher"/>.
/// Set once at startup by the host (DI container) before any static helpers are called.
/// The generator emits calls to <see cref="Current"/> in every static helper method.
/// </summary>
public static class YFexDispatcherProvider
{
    private static IDispatcher? _current;

    public static IDispatcher Current =>
        _current ?? throw new InvalidOperationException(
            "YFex dispatcher is not configured. Call services.AddYFexCqrs() in Program.cs.");

    /// <summary>Called by the DI registration extension after building the service provider.</summary>
    public static void Set(IDispatcher dispatcher) =>
        _current = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    /// <summary>Resets the provider — for testing only.</summary>
    public static void Reset() => _current = null;
}
