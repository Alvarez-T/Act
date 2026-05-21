using System.Windows.Input;
using YFex.State.Commands;

namespace YFex.State.Mvvm;

/// <summary>
/// Bridges a parameterless <see cref="IStateCommand"/> to <see cref="ICommand"/> for XAML binding.
/// </summary>
public sealed class MvvmCommandAdapter : ICommand
{
    private readonly IStateCommand _command;

    public MvvmCommandAdapter(IStateCommand command)
    {
        _command = command;
        _command.CanExecuteChanged += OnCanExecuteChanged;
    }

    public event EventHandler? CanExecuteChanged;

    private void OnCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    bool ICommand.CanExecute(object? parameter) => _command.CanExecute();
    void ICommand.Execute(object? parameter) => _command.Execute();
}

/// <summary>
/// Bridges a typed <see cref="IStateCommand{T}"/> to <see cref="ICommand"/> for XAML binding.
/// Compiled binding systems (WinUI x:Bind, Avalonia CompiledBinding, MAUI compiled bindings)
/// bind to the typed <see cref="CanExecute(T)"/>/<see cref="Execute(T)"/> methods when the
/// parameter type is known at compile time, bypassing the object boundary entirely.
/// </summary>
public sealed class MvvmCommandAdapter<T> : ICommand
{
    private readonly IStateCommand<T> _command;

    public MvvmCommandAdapter(IStateCommand<T> command)
    {
        _command = command;
        _command.CanExecuteChanged += OnCanExecuteChanged;
    }

    public event EventHandler? CanExecuteChanged;

    private void OnCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    // ── Typed fast path (zero boxing for value-type parameters) ─────────────
    public bool CanExecute(T parameter) => _command.CanExecute(parameter);
    public void Execute(T parameter) => _command.Execute(parameter);

    // ── Legacy object path (XAML {Binding} markup, WPF DataContext binding) ─
    bool ICommand.CanExecute(object? parameter) => _command.CanExecute((T)parameter!);
    void ICommand.Execute(object? parameter) => _command.Execute((T)parameter!);
}
