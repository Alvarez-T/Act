using CommunityToolkit.Mvvm.Messaging;
using NavigatR.CommunityToolkit;

namespace Act.SignalR.Client;

public delegate void HubMessageHandler<in TEvent>(TEvent @event)
    where TEvent : class;

public delegate void HubMessageHandler<in TRecipient, in TEvent>(TRecipient recipient, TEvent @event)
    where TRecipient : class
    where TEvent : class;

public class BroadcastHubMessage<T> : BroadcastMessage<T>
    where T : class
{
    private readonly IHubClient _hubClient;

    internal BroadcastHubMessage(T recipient, IHubClient hubClient) : base(recipient)
    {
        _hubClient = hubClient;
        Recipient = recipient;
    }

    public BroadcastHubMessage<T> On<TMessage>(HubMessageHandler<T, TMessage> handler)
        where TMessage : class
    {
        var messageHandler = new MessageHandler<T, TMessage>(handler);
        OnViewMessage(messageHandler);
        OnHubMessage(handler);
        return this;
    }

    public BroadcastHubMessage<T> OnHubMessage<TMessage>(HubMessageHandler<T, TMessage> hubMessageHandler)
        where TMessage : class
    {
        _hubClient.Register(Recipient, hubMessageHandler);
        return this;
    }

}

public static class BroadcastHubMessage
{
    public static BroadcastHubMessage<T> To<T>(T recipient, IHubClient hubClient)
        where T : class => new(recipient, hubClient);

    public static BroadcastHubMessage<T> To<T>(this BroadcastMessage<T> broadcast, T recipient, IHubClient hubClient)
        where T : class => new(recipient, hubClient);

    public static BroadcastHubMessage<T> RegisterHub<T>(this BroadcastMessage<T> broadcast, IHubClient hubClient)
        where T : class => new(broadcast.Recipient, hubClient);

}