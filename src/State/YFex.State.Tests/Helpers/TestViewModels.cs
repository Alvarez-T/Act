using System;
using System.Threading.Tasks;
using YFex.State.Mvvm;
using YFex.State.Notification;

namespace YFex.State.Tests.Helpers;

/// <summary>Codegen-driven VMs covering the principal attribute combinations.</summary>
public partial class PersonVm : StateObject
{
    [Observable] public partial string FirstName { get; set; }
    [Observable] public partial string LastName { get; set; }
    [Observable] public partial int Age { get; set; }
}

public partial class MvvmPersonVm : MvvmStateObject
{
    [Observable] public partial string FirstName { get; set; }
    [Observable] public partial string LastName { get; set; }
    [Observable] public partial int Age { get; set; }
}

public partial class AsyncResultVm : StateObject
{
    [Observable, NotifyOnTaskCompletion]
    public partial Task<int>? Result { get; set; }
}

public partial class AsyncTaskVm : StateObject
{
    [Observable, NotifyOnTaskCompletion]
    public partial Task? Operation { get; set; }
}

public partial class MvvmAsyncResultVm : MvvmStateObject
{
    [Observable, NotifyOnTaskCompletion]
    public partial Task<int>? Result { get; set; }
}

/// <summary>Inheritance test: derived VM adds more properties; PropertyIds must not collide.</summary>
public partial class DerivedMvvmPersonVm : MvvmPersonVm
{
    [Observable] public partial string Email { get; set; }
    [Observable] public partial bool IsAdmin { get; set; }
}

public sealed class CountingHandler : IChangedHandler
{
    public int OnChangedCount;
    public int OnChangingCount;
    public int OnFlushStartingCount;
    public int OnFlushCompletedCount;

    public void OnChanged(object source, in ChangedNotification notification) => OnChangedCount++;
    public void OnChanging(object source, in ChangedNotification notification) => OnChangingCount++;
    public void OnBatchFlushStarting(object source) => OnFlushStartingCount++;
    public void OnBatchFlushCompleted(object source) => OnFlushCompletedCount++;
}

public sealed class ThrowingHandler : IChangedHandler
{
    public Exception OnChangedException { get; init; } = new InvalidOperationException("boom");
    public Exception? OnChangingException { get; init; }

    public void OnChanged(object source, in ChangedNotification notification)
        => throw OnChangedException;
    public void OnChanging(object source, in ChangedNotification notification)
    {
        if (OnChangingException is not null) throw OnChangingException;
    }
}
