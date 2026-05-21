using Wolverine;

namespace YFex.Cqrs;

/// <summary>
/// Locator for IMessagBus, allowing you to send commands without directly depending on the message bus implementation.
/// </summary>
public static class Command
{
    public static Task<Result> Execute(ICommand command)
        => MessageBusProvider.Current.InvokeAsync<Result>(command);

    /// <summary>
    /// Allows you to set a mock message bus for testing purposes.
    /// </summary>
    /// <param name="mockBus"></param>
    internal static void SetMock(IMessageBus mockBus)
        => MessageBusProvider.Set(mockBus);
}
