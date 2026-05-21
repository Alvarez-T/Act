using System;
using System.Collections.Generic;
using System.Text;

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

/// <summary>Navigation was blocked because CanNavigate() returned false.</summary>
public record NavigationDenied(string? Reason = null);

/// <summary>Navigation was cancelled via CancellationToken, or the navigable called Returns() with no value.</summary>
public record NavigationCancelled();