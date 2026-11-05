using System;
using System.Collections.Generic;
using System.Text;
using YFex.Authorization.Public;

namespace YFex.NavigatR;

/// <summary>
/// Represents the outcome of an awaited navigation.
/// Exhaustive pattern matching enforced by the compiler — no nulls, no exceptions, no bool flags.
/// </summary>
public readonly union NavigationResult(
    NavigationSuccess,
    NavigationDenied,
    NavigationCancelled
)
{
    public static NavigationSuccess Ok() => new NavigationSuccess();
    public static NavigationSuccess<TResult> Ok<TResult>(TResult result) => new NavigationSuccess<TResult>(result);
    public static NavigationDenied Deny(string? reason = null) => new NavigationDenied(reason);
    public static NavigationCancelled Cancel() => new NavigationCancelled();
}

/// <summary>
/// Represents the outcome of an awaited navigation.
/// Exhaustive pattern matching enforced by the compiler — no nulls, no exceptions, no bool flags.
/// </summary>
/// <typeparam name="TResult">The type of value returned on success.</typeparam>
public readonly union NavigationResult<TResult>(
    NavigationSuccess<TResult>,
    NavigationDenied,
    NavigationCancelled
);


/// <summary>Navigation completed and the navigable returned a value.</summary>
public record NavigationSuccess();

/// <summary>Navigation completed and the navigable returned a value.</summary>
public record NavigationSuccess<TResult>(TResult Value) : NavigationSuccess;

/// <summary>
/// Navigation was blocked: by the view model's <c>OnNavigation</c> guard, or by the route's authorization policy. For an
/// authorization denial <see cref="Verdict"/> is the evaluator's answer (UIs build their own text from its kind and the missing
/// permission's catalog display name) and <see cref="Reason"/> is the default text ("Requer: Cancelar venda"); otherwise
/// <see cref="Verdict"/> is <c>default</c> (<see cref="AuthorizationVerdictKind.None"/>).
/// </summary>
public record NavigationDenied(string? Reason = null, AuthorizationVerdict Verdict = default)
{
    /// <summary>True when the route's authorization policy denied the navigation.</summary>
    public bool IsAuthorizationDenial => Verdict.HasValue;
}

/// <summary>Navigation was cancelled via CancellationToken, or the navigable called Returns() with no value.</summary>
public record NavigationCancelled();