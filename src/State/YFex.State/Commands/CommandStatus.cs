namespace YFex.State.Commands;

/// <summary>
/// Unified execution state for async state commands. Eliminates multiple boolean checks
/// (IsRunning, IsFaulted, etc.) in favour of a single, declaratively bindable value.
/// <para>
/// UI bindings examples:
/// <list type="bullet">
///   <item>WPF/MAUI: <c>&lt;DataTrigger Binding="{Binding SaveCommand.Status}" Value="Executing"&gt;</c></item>
///   <item>Blazor: <c>@if (SaveCommand.Status == CommandStatus.Executing)</c></item>
/// </list>
/// </para>
/// </summary>
public enum CommandStatus : byte
{
    /// <summary>The command has not been executed, or has been reset after a previous run.</summary>
    Idle = 0,

    /// <summary>The command is currently executing asynchronously.</summary>
    Executing = 1,

    /// <summary>The last execution completed successfully without throwing.</summary>
    Succeeded = 2,

    /// <summary>The last execution threw an unhandled exception.</summary>
    Faulted = 3,

    /// <summary>
    /// The last execution was cancelled — the method threw
    /// <see cref="System.OperationCanceledException"/>.
    /// </summary>
    Canceled = 4,
}
