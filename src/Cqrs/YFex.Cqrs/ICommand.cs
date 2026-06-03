namespace YFex.Cqrs;

public interface ICommand
{
}

/// <summary>
/// A command that returns <typeparamref name="TResult"/> on success.
/// Extends <see cref="ICommand"/> so command pipelines that handle
/// both void and result-bearing commands share the same base constraint.
/// </summary>
public interface ICommand<out TResult> : ICommand
{
}
