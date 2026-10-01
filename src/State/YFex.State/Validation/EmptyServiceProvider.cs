namespace YFex.State.Validation;

/// <summary>Service provider with no services — default for <c>StateObject.ValidationServices</c>.</summary>
public sealed class EmptyServiceProvider : IServiceProvider
{
    public static readonly EmptyServiceProvider Instance = new();

    private EmptyServiceProvider()
    {
    }

    public object? GetService(Type serviceType) => null;
}
