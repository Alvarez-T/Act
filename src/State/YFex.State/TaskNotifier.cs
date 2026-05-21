namespace YFex.State;

/// <summary>
/// Internal contract used by the private <see cref="StateObject.SetFieldAndNotifyOnCompletion"/> helper
/// so a single generic implementation can serve both <see cref="TaskNotifier"/> and <see cref="TaskNotifier{T}"/>.
/// </summary>
internal interface ITaskNotifier<TTask> where TTask : Task
{
    TTask? Task { get; set; }
}

/// <summary>
/// Backing-field wrapper for a <see cref="System.Threading.Tasks.Task"/> observable property.
/// Use as the field type with <see cref="StateObject.SetFieldAndNotifyOnCompletion"/> to re-fire
/// <c>PropertyChanged</c> when the task completes.
/// </summary>
public sealed class TaskNotifier : ITaskNotifier<Task>
{
    internal TaskNotifier() { }

    private Task? _task;

    Task? ITaskNotifier<Task>.Task
    {
        get => _task;
        set => _task = value;
    }

    /// <summary>Unwraps the stored <see cref="Task"/> value.</summary>
    public static implicit operator Task?(TaskNotifier? notifier) => notifier?._task;
}

/// <summary>
/// Backing-field wrapper for a <see cref="System.Threading.Tasks.Task{T}"/> observable property.
/// </summary>
/// <typeparam name="T">The result type of the task.</typeparam>
public sealed class TaskNotifier<T> : ITaskNotifier<Task<T>>
{
    internal TaskNotifier() { }

    private Task<T>? _task;

    Task<T>? ITaskNotifier<Task<T>>.Task
    {
        get => _task;
        set => _task = value;
    }

    /// <summary>Unwraps the stored <see cref="Task{T}"/> value.</summary>
    public static implicit operator Task<T>?(TaskNotifier<T>? notifier) => notifier?._task;
}
