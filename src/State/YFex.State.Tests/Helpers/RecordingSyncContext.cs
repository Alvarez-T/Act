using System;
using System.Collections.Generic;
using System.Threading;

namespace YFex.State.Tests.Helpers;

/// <summary>
/// SynchronizationContext that records every <see cref="Post"/> and <see cref="Send"/> call
/// before invoking the callback inline. Tests can inspect <see cref="Posts"/> and
/// <see cref="Sends"/> after the work runs to verify marshaling behaviour.
/// </summary>
public sealed class RecordingSyncContext : SynchronizationContext
{
    private readonly List<Recorded> _posts = new();
    private readonly List<Recorded> _sends = new();
    private readonly bool _runInline;

    public RecordingSyncContext(bool runInline = true) => _runInline = runInline;

    public IReadOnlyList<Recorded> Posts => _posts;
    public IReadOnlyList<Recorded> Sends => _sends;
    public int PostCount => _posts.Count;
    public int SendCount => _sends.Count;

    public override void Post(SendOrPostCallback d, object? state)
    {
        _posts.Add(new Recorded(d, state, Environment.CurrentManagedThreadId));
        if (_runInline) d(state);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        _sends.Add(new Recorded(d, state, Environment.CurrentManagedThreadId));
        if (_runInline) d(state);
    }

    public void Reset()
    {
        _posts.Clear();
        _sends.Clear();
    }

    public readonly record struct Recorded(SendOrPostCallback Callback, object? State, int ThreadId);
}
