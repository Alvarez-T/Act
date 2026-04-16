using System;
using System.Collections.Generic;
using System.Text;

namespace YFex.NavigatR;

/// <summary>
/// Represents the outcome of an awaited navigation.
/// Exhaustive pattern matching enforced by the compiler — no nulls, no exceptions, no bool flags.
/// </summary>
/// <typeparam name="TResult">The type of value returned on success.</typeparam>
public union NavigationResult<TResult>(
    NavigationSuccess<TResult>,
    NavigationDenied,
    NavigationCancelled
);

/// <summary>Navigation completed and the navigable returned a value.</summary>
public record NavigationSuccess<TResult>(TResult Value);

/// <summary>Navigation was blocked because CanNavigate() returned false.</summary>
public record NavigationDenied();

/// <summary>Navigation was cancelled via CancellationToken, or the navigable called Returns() with no value.</summary>
public record NavigationCancelled();
