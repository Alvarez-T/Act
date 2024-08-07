namespace Act.SignalR.Client;



public class HubClientBuilder<THandler> where THandler : IHubClient
{
    private readonly Dictionary<Type, Delegate> handlers = [];
    public THandler Build()
    {
        throw new NotImplementedException();
    }

    public HubClientBuilder<THandler> AssignHandler<TMessage>(HubMessageHandler<TMessage> messageHandler)
        where TMessage : class
    {
        handlers.TryAdd(typeof(TMessage), messageHandler);
        return this;
    }
}