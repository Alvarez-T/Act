namespace YFex.NavigatR;

/// <summary>
/// Marks a ViewModel for automatic Route generation and registration.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class RouteAttribute : Attribute
{
    public string? RouteName { get; }
    public Type? RouteType { get; }
    public string? DisplayName { get; set; }

    /// <summary>
    /// Declares the parameter type for this route.
    /// The generator will:
    /// - Add a constructor parameter to the generated route record
    /// - Generate the typed OnNavigation partial on the ViewModel
    /// - Generate a file-scoped GetParameter() extension
    /// </summary>
    public Type? Parameter { get; set; }

    /// <summary>
    /// Whether the parameter is required. Default true.
    /// When false — constructor parameter is nullable with default null,
    /// and the generated OnNavigation partial receives T? instead of T.
    /// </summary>
    public bool ParameterRequired { get; set; } = true;

    public RouteAttribute(string routeName) => RouteName = routeName;
    public RouteAttribute(Type routeType) => RouteType = routeType;
}