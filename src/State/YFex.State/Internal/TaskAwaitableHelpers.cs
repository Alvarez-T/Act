using System.Runtime.CompilerServices;

namespace YFex.State.Internal;

internal static class TaskAwaitableHelpers
{
    /// <summary>
    /// Returns an awaiter for the task that suppresses exception observation.
    /// Used by <see cref="StateObject"/>'s task-monitoring path so faulted tasks do not
    /// crash the process through the <c>async void</c> <c>MonitorTask</c> helper.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static SuppressExceptionAwaiter GetAwaitableWithoutEndValidation(this Task task)
        => new(task.GetAwaiter());

    internal readonly struct SuppressExceptionAwaiter : INotifyCompletion
    {
        private readonly TaskAwaiter _inner;

        internal SuppressExceptionAwaiter(TaskAwaiter inner) => _inner = inner;

        /// <summary>Makes the struct directly awaitable: <c>await task.GetAwaitableWithoutEndValidation()</c>.</summary>
        public SuppressExceptionAwaiter GetAwaiter() => this;

        public bool IsCompleted => _inner.IsCompleted;

        public void OnCompleted(Action continuation) => _inner.OnCompleted(continuation);

        public void GetResult()
        {
            try { _inner.GetResult(); }
            catch { /* intentionally swallowed — callers only care about completion, not outcome */ }
        }
    }
}
