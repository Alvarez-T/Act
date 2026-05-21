using System;
using System.Threading;
using System.Threading.Tasks;

namespace YFex.State.Commands;

/// <summary>Synchronous parameterless command.</summary>
public interface IStateCommand
{
    bool CanExecute();
    void Execute();
    event Action? CanExecuteChanged;
}

/// <summary>
/// Synchronous command with a typed parameter.
/// <c>allows ref struct</c> is deliberately absent here — ref struct types cannot be boxed
/// (breaking XAML binding), cannot survive an await (breaking async commands), and cannot be
/// stored in a field (breaking [Queue]). See <see cref="IRefStructCommand{T}"/> for the narrow case.
/// </summary>
public interface IStateCommand<in T>
{
    bool CanExecute(T parameter);
    void Execute(T parameter);
    event Action? CanExecuteChanged;
}

/// <summary>
/// Async command. <see cref="IsExecuting"/> and lock-free concurrency gate are handled by the
/// generated implementation via <c>Interlocked.CompareExchange</c> — one CPU instruction, no allocation.
/// </summary>
public interface IAsyncStateCommand : IStateCommand
{
    Task ExecuteAsync(CancellationToken ct = default);
    bool IsExecuting { get; }

    /// <summary>
    /// Unified execution state for UI binding. The generated implementation fires
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> on every transition.
    /// Default implementation (for hand-written commands) derives from <see cref="IsExecuting"/>.
    /// </summary>
    CommandStatus Status => IsExecuting ? CommandStatus.Executing : CommandStatus.Idle;
}

#if NET9_0_OR_GREATER
/// <summary>
/// Opt-in interface for the narrow case where <c>allows ref struct</c> is safe:
/// sync-only, not bound via XAML, not queued, parameter not stored in a field.
/// Requires .NET 9+ because <c>allows ref struct</c> is a runtime-level feature.
/// </summary>
public interface IRefStructCommand<T> where T : allows ref struct
{
    bool CanExecute(T parameter);
    void Execute(T parameter);
    // No CanExecuteChanged: boxing a ref struct is illegal.
    // No async variant: cannot await with a ref struct in scope.
    // No [Queue]: cannot store a ref struct in a field.
}
#endif
