using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace YFex.NavigatR;

/// <summary>
/// Bridges the INavigable.Returns() call to the pending TaskCompletionSource
/// without requiring the navigable to know about the TCS directly.
///
/// When a navigable calls Returns(result), the interceptor fires the registered
/// callback which completes the awaited Task on the caller side.
///
/// Uses a ConditionalWeakTable so registrations are tied to the navigable's lifetime —
/// no memory leaks when instances are GC'd.
/// </summary>
internal static class NavigableReturnInterceptor
{
    private static readonly ConditionalWeakTable<INavigable, CallbackEntry> _callbacks = new();

    /// <summary>
    /// Registers a callback to be invoked when the given navigable calls Returns().
    /// </summary>
    internal static void Register(INavigable navigable, Action<object?> onReturn)
    {
        _callbacks.AddOrUpdate(navigable, new CallbackEntry(onReturn));
    }

    /// <summary>
    /// Called by the navigator when it intercepts a Returns() invocation from a navigable.
    /// Fires the registered callback if one exists.
    /// </summary>
    internal static void NotifyReturn(INavigable navigable, object? result)
    {
        if (_callbacks.TryGetValue(navigable, out var entry))
        {
            entry.Callback(result);
            _callbacks.Remove(navigable);
        }
    }

    /// <summary>
    /// Removes any pending callback for the given navigable.
    /// Called on context clear or close to prevent stale completions.
    /// </summary>
    internal static void Unregister(INavigable navigable)
    {
        _callbacks.Remove(navigable);
    }

    private sealed class CallbackEntry
    {
        public Action<object?> Callback { get; }
        public CallbackEntry(Action<object?> callback) => Callback = callback;
    }
}
