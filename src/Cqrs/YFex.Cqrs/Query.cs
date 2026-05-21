using Wolverine;

namespace YFex.Cqrs;

/// <summary>
/// Locator for IMessageBus, allowing you to execute queries without directly depending on the message bus implementation.
/// </summary>
public static class Query
{
    public static Task<Result<TResult>> Execute<TResult>(IQuery<TResult> query)
        => MessageBusProvider.Current.InvokeAsync<Result<TResult>>(query);

    /// <summary>
    /// Allows you to set a mock message bus for testing purposes.
    /// </summary>
    /// <param name="mockBus"></param>
    internal static void SetMock(IMessageBus mockBus)
        => MessageBusProvider.Set(mockBus);
}
