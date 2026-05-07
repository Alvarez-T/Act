using System;

namespace YFex.NavigatR.SourceGenerator
{
    internal enum RouteMode { AutoGenerate, Manual }

    internal readonly record struct RouteViewModel(
        string Namespace,
        string ViewModelName,
        bool IsPartial,
        bool ImplementsINavigable,
        RouteMode Mode,
        string? RouteName,
        string? GeneratedRouteClassName,
        string? ManualRouteTypeName,
        bool ManualRouteTypeIsValid,
        /// <summary>From [Route(Parameter = typeof(T))] — null if not declared.</summary>
        string? ParamsTypeName,
        /// <summary>Whether the parameter is required at the NavigateTo call site. Default true.</summary>
        bool ParameterRequired,
        /// <summary>TResult from INavigable&lt;TResult&gt; — null if not implemented.</summary>
        string? ResultTypeName,
        bool ProducesResult,
        string? DisplayName
    ) : IEquatable<RouteViewModel>;

    internal readonly record struct RegistrationModel(
        EquatableArray<RegistrationEntry> Entries
    ) : IEquatable<RegistrationModel>;

    internal readonly record struct RegistrationEntry(
        string RouteFullName,
        string ViewModelFullName
    ) : IEquatable<RegistrationEntry>;
}

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
