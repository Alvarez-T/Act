namespace YFex.NavigatR;

/// <summary>
/// Marks a ViewModel class for automatic Route generation and registration.
/// <para>
/// Usage A — auto-generate a Route record from a name string:
/// <code>[Route("order")]</code>
/// generates <c>OrderRoute : IRoute&lt;TParams, TResult&gt;</c> in the same namespace.
/// </para>
/// <para>
/// Usage B — use an existing Route type (only the registration is generated):
/// <code>[Route(typeof(MyRoute))]</code>
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class RouteAttribute : Attribute
{
    /// <summary>
    /// The route name used to derive the generated Route class name (auto-generate mode).
    /// E.g. <c>"order"</c> → <c>OrderRoute</c>.
    /// </summary>
    public string? RouteName { get; }

    /// <summary>
    /// An existing <see cref="IRoute"/> implementation to use (manual mode).
    /// Only a registration call is generated; no new Route class is created.
    /// </summary>
    public Type? RouteType { get; }

    /// <summary>
    /// Optional display name written into the generated Route's <c>DisplayName</c> property.
    /// Only applies in auto-generate mode.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Auto-generate mode: derives Route class name from <paramref name="routeName"/>.
    /// </summary>
    public RouteAttribute(string routeName) => RouteName = routeName;

    /// <summary>
    /// Manual mode: uses the supplied <paramref name="routeType"/> instead of generating one.
    /// </summary>
    public RouteAttribute(Type routeType) => RouteType = routeType;
}