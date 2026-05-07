namespace YFex.NavigatR;

/// <summary>
/// Marks a ViewModel for automatic Route generation and registration.
/// </summary>
/// <example>
/// No parameter, no result:
/// <code>[Route("home")]</code>
///
/// With parameter:
/// <code>[Route("order", Parameter = typeof(OrderParams))]</code>
///
/// Manual route type:
/// <code>[Route(typeof(MyRoute))]</code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class RouteAttribute : Attribute
{
    public string? RouteName { get; }
    public Type? RouteType { get; }
    public string? DisplayName { get; set; }

    /// <summary>
    /// Declares the parameter type for this route.
    /// The generator will:
    /// <list type="bullet">
    /// <item>Implement <c>INavigableAccepts&lt;TParameter&gt;</c> on the ViewModel</item>
    /// <item>Generate a file-scoped <c>GetParameter()</c> extension on <see cref="NavigationContext"/></item>
    /// <item>Generate explicit <c>INavigable.OnNavigation</c> bridge + enforce typed partial</item>
    /// <item>Wire <c>IRouteAccepts&lt;TParameter&gt;</c> on the generated Route (when required)</item>
    /// </list>
    /// </summary>
    public Type? Parameter { get; set; }

    /// <summary>
    /// Whether the parameter is required at the NavigateTo call site.
    /// Default: <c>true</c> — compiler enforces the parameter must be passed.
    /// When <c>false</c> — parameter is optional, <c>NavigateTo(route)</c> is also valid,
    /// and the generated partial receives <c>TParameter?</c>.
    /// </summary>
    public bool ParameterRequired { get; set; } = true;

    /// <summary>Auto-generate mode: derives Route class name from <paramref name="routeName"/>.</summary>
    public RouteAttribute(string routeName) => RouteName = routeName;

    /// <summary>Manual mode: uses the supplied <paramref name="routeType"/>.</summary>
    public RouteAttribute(Type routeType) => RouteType = routeType;
}