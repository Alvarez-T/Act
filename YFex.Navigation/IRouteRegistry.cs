using System.Text.RegularExpressions;

namespace YFex.NavigatR;

/// <summary>
/// Maps route strings to navigable types.
/// Supports static routes ("/home") and single-parameter routes ("/product/{id}").
/// Parameter values are extracted as strings — the navigable is responsible for parsing.
///
/// Registered as Singleton — one registry shared across all navigation contexts.
/// </summary>
public sealed class RouteRegistry
{
    private readonly List<RoutePattern> _patterns = new();

    /// <summary>
    /// Registers a URL-style string pattern to a ViewModel type.
    /// </summary>
    public void Register(string pattern, Type navigableType)
    {
        _patterns.Add(new RoutePattern(pattern, navigableType));
    }

    /// <summary>
    /// Resolves a URL-style string route to a <see cref="RouteEntry"/>.
    /// Returns <c>null</c> if no pattern matches.
    /// </summary>
    public RouteEntry? Resolve(string route)
    {
        foreach (var pattern in _patterns)
        {
            if (pattern.TryMatch(route, out var parameter))
                return new RouteEntry(pattern.NavigableType, parameter);
        }
        return null;
    }

    private readonly Dictionary<Type, Type> _routeToViewModel = new();

    /// <summary>
    /// Registers a typed <typeparamref name="TRoute"/> → <typeparamref name="TViewModel"/> mapping.
    /// This is normally called automatically by the generated
    /// <c>NavigatRRegistration.RegisterAll()</c> method.
    /// </summary>
    public void Register<TRoute, TViewModel>()
        where TRoute : IRoute
        where TViewModel : INavigable
    {
        _routeToViewModel[typeof(TRoute)] = typeof(TViewModel);
    }

    /// <summary>
    /// Resolves the ViewModel <see cref="Type"/> for the given <paramref name="route"/> instance.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no registration exists for the route's type.
    /// </exception>
    public Type ResolveViewModel(IRoute route)
    {
        var routeType = route.GetType();
        if (_routeToViewModel.TryGetValue(routeType, out var vmType))
            return vmType;

        throw new InvalidOperationException(
            $"No ViewModel registered for route '{routeType.Name}'. " +
            $"Ensure the ViewModel is annotated with [Route] and that " +
            $"NavigatRRegistration.RegisterAll(registry) is called at startup.");
    }

    /// <summary>
    /// The resolved entry for a string-based route, containing the target ViewModel type
    /// and any extracted route parameter.
    /// </summary>
    /// <param name="RouteType">The ViewModel type to instantiate for this route.</param>
    /// <param name="Parameter">An optional parameter extracted from the route pattern match.</param>
    private sealed class RoutePattern
    {
        private readonly Regex _regex;
        private readonly string? _parameterName;

        public Type NavigableType { get; }

        public RoutePattern(string pattern, Type navigableType)
        {
            NavigableType = navigableType;

            var paramMatch = Regex.Match(pattern, @"\{(\w+)\}");

            if (paramMatch.Success)
            {
                _parameterName = paramMatch.Groups[1].Value;
                var regexPattern = Regex.Escape(pattern)
                    .Replace(Regex.Escape($"{{{_parameterName}}}"), $@"(?<{_parameterName}>[^/]+)");
                _regex = new Regex($"^{regexPattern}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            }
            else
            {
                _parameterName = null;
                _regex = new Regex($"^{Regex.Escape(pattern)}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            }
        }

        public bool TryMatch(string route, out object? parameter)
        {
            parameter = null;
            var match = _regex.Match(route);

            if (!match.Success) return false;

            if (_parameterName is not null)
                parameter = match.Groups[_parameterName].Value;

            return true;
        }
    }
}

public record RouteEntry(Type RouteType, object? Parameter);