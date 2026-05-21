namespace YFex.State;

/// <summary>
/// Scope returned by <see cref="StateObject{TSelf}.BeginUpdate"/>.
/// ref struct enforces stack-only lifetime; the compiler prevents capture by lambdas or async methods.
/// Mutations inside a scope are batched — a single dispatch fires when the scope exits.
/// </summary>
public ref struct BatchScope
{
    private readonly StateObject _owner;

    internal BatchScope(StateObject owner)
    {
        _owner = owner;
        owner.EnterUpdate();
    }

    public void Dispose() => _owner.ExitUpdate();
}
