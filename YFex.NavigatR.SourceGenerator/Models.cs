using System;
using YFex.NavigatR.SourceGenerator;

namespace YFex.NavigatR.Generator
{

    internal enum RouteMode { AutoGenerate, Manual }

    /// <summary>
    /// Describes a single [Prefetch]-marked method on a ViewModel.
    /// </summary>
    internal readonly record struct PrefetchMethod(
        /// <summary>Original method name e.g. "FetchOrder"</summary>
        string MethodName,
        /// <summary>Fully qualified return type if Task&lt;T&gt;, null if Task (warm-only)</summary>
        string? ReturnTypeName,
        /// <summary>Whether this method produces a value to inject into OnNavigation</summary>
        bool ProducesValue
    ) : IEquatable<PrefetchMethod>;

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
        string? ParamsTypeName,
        bool ParameterRequired,
        string? ResultTypeName,
        bool ProducesResult,
        string? DisplayName,
        EquatableArray<PrefetchMethod> PrefetchMethods
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
