using System;
using System.Threading;

namespace YFex.State.Tests.Helpers;

/// <summary>
/// Saves and restores <see cref="SynchronizationContext.Current"/> around a test.
/// </summary>
public sealed class SyncContextScope : IDisposable
{
    private readonly SynchronizationContext? _previous;

    public SyncContextScope(SynchronizationContext? @new)
    {
        _previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(@new);
    }

    public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
}
