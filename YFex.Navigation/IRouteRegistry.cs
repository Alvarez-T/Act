using System.Reflection;
using System.Text.RegularExpressions;

namespace YFex.NavigatR;

/// <summary>
/// Maps route strings to navigable types and typed routes to ViewModels.
/// Registered as Singleton — one registry shared across all navigation contexts.
/// </summary>
public sealed class RouteRegistry
{
    // -----------------------------------------------------------------------
    // Typed route → ViewModel
    // -----------------------------------------------------------------------

    private readonly Dictionary<Type, Type> _routeToViewModel = new();
    private readonly Dictionary<Type, Type> _viewModelToRoute = new();

    /// <summary>
    /// Registers a typed route → ViewModel mapping.
    /// Called automatically by NavigatRRegistration.RegisterAll().
    /// </summary>
    public void Register<TRoute, TViewModel>()
        where TRoute : IRoute
        where TViewModel : INavigable
    {
        _routeToViewModel[typeof(TRoute)] = typeof(TViewModel);
        _viewModelToRoute[typeof(TViewModel)] = typeof(TRoute);
    }

    /// <summary>
    /// Resolves the ViewModel type for the given route instance.
    /// </summary>
    public Type ResolveViewModel(IRoute route)
    {
        if (_routeToViewModel.TryGetValue(route.GetType(), out var vmType))
            return vmType;

        throw new InvalidOperationException(
            $"No ViewModel registered for route '{route.GetType().Name}'. " +
            $"Ensure [Route] is applied and NavigatRRegistration.RegisterAll() is called at startup.");
    }

    /// <summary>
    /// Resolves the Route type for the given ViewModel type.
    /// Used by string route resolution to find the typed route to construct.
    /// </summary>
    public Type? ResolveRouteType(Type viewModelType)
        => _viewModelToRoute.TryGetValue(viewModelType, out var routeType) ? routeType : null;

    // -----------------------------------------------------------------------
    // String pattern → ViewModel
    // -----------------------------------------------------------------------

    private readonly List<RoutePattern> _patterns = new();

    /// <summary>
    /// Registers a URL-style string pattern to a ViewModel type.
    /// The URL segment is automatically parsed to the route's parameter type
    /// if it is string, a primitive, or implements IParsable&lt;T&gt;.
    /// </summary>
    public void Register(string pattern, Type navigableType)
        => _patterns.Add(new RoutePattern(pattern, navigableType, fixedParameter: null));

    /// <summary>
    /// Registers a URL-style string pattern with a fixed parameter object.
    /// Use when the route parameter is a complex type that cannot be parsed from a URL segment.
    /// </summary>
    public void Register(string pattern, Type navigableType, object fixedParameter)
        => _patterns.Add(new RoutePattern(pattern, navigableType, fixedParameter));

    /// <summary>
    /// Resolves a URL-style string route.
    /// Returns null if no pattern matches.
    /// </summary>
    public StringRouteEntry? Resolve(string route)
    {
        foreach (var pattern in _patterns)
            if (pattern.TryMatch(route, out var rawParam))
                return new StringRouteEntry(pattern.NavigableType, rawParam);
        return null;
    }

    // -----------------------------------------------------------------------
    // Inner types
    // -----------------------------------------------------------------------

    private sealed class RoutePattern
    {
        private readonly Regex _regex;
        private readonly string? _parameterName;
        private readonly object? _fixedParameter;

        public Type NavigableType { get; }

        public RoutePattern(string pattern, Type navigableType, object? fixedParameter)
        {
            NavigableType = navigableType;
            _fixedParameter = fixedParameter;

            var paramMatch = Regex.Match(pattern, @"\{(\w+)\}");

            if (paramMatch.Success)
            {
                _parameterName = paramMatch.Groups[1].Value;
                var regexPattern = Regex.Escape(pattern)
                    .Replace(Regex.Escape($"{{{_parameterName}}}"),
                        $@"(?<{_parameterName}>[^/]+)");
                _regex = new Regex(
                    $"^{regexPattern}$",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase);
            }
            else
            {
                _parameterName = null;
                _regex = new Regex(
                    $"^{Regex.Escape(pattern)}$",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase);
            }
        }

        public bool TryMatch(string route, out object? parameter)
        {
            parameter = null;
            var match = _regex.Match(route);
            if (!match.Success) return false;

            // Case 3 — fixed object registered explicitly
            if (_fixedParameter is not null)
            {
                parameter = _fixedParameter;
                return true;
            }

            // Cases 1 & 2 — extract string segment, resolve type later
            if (_parameterName is not null)
                parameter = match.Groups[_parameterName].Value; // raw string for now

            return true;
        }
    }
}

/// <summary>
/// Result of string route resolution.
/// RawParameter is either:
/// - null (no parameter)
/// - a string segment extracted from the URL (to be parsed to the route's param type)
/// - a pre-built object (registered via Register(pattern, type, fixedParam))
/// </summary>
public record StringRouteEntry(Type ViewModelType, object? RawParameter);