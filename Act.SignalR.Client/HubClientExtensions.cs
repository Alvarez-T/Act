using Act.Internals;
using CommunityToolkit.Mvvm.Messaging;

namespace Act.SignalR.Client;

internal static class HubClientExtensions
{
    internal static void Register<TRecipient, TMessage>(this IHubClient hubClient, TRecipient recipient, HubMessageHandler<TRecipient, TMessage> hubMessageHandler)
        where TRecipient : class
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(hubClient);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(hubMessageHandler);

        ((HubClient)hubClient).Register(recipient, default(Unit), hubMessageHandler);
    }
}