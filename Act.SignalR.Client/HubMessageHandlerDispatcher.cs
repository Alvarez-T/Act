//Source: https://github.com/CommunityToolkit/dotnet/blob/7b53ae23dfc6a7fb12d0fc058b89b6e948f48448/src/CommunityToolkit.Mvvm/Messaging/Internals/MessageHandlerDispatcher.cs

using CommunityToolkit.Mvvm.Messaging;
using System.Runtime.CompilerServices;

namespace Act.SignalR.Client;

/// <summary>
/// A dispatcher type that invokes a given <see cref="MessageHandler{TRecipient, TMessage}"/> callback.
/// </summary>
/// <remarks>
/// This type is used to avoid type aliasing with <see cref="Unsafe.As{T}(object)"/> when the generic
/// arguments are not known. Additionally, this is an abstract class and not an interface so that when
/// <see cref="Invoke(object, object)"/> is called, virtual dispatch will be used instead of interface
/// stub dispatch, which is much slower and with more indirections.
/// </remarks>
internal abstract class HubMessageHandlerDispatcher
{
    /// <summary>
    /// Invokes the current callback on a target recipient, with a specified message.
    /// </summary>
    /// <param name="recipient">The target recipient for the message.</param>
    /// <param name="message">The message being broadcast.</param>
    public abstract void Invoke(object recipient, object message);

    /// <summary>
    /// A generic version of <see cref="HubMessageHandlerDispatcher"/>.
    /// </summary>
    /// <typeparam name="TRecipient">The type of recipient for the message.</typeparam>
    /// <typeparam name="TMessage">The type of message to receive.</typeparam>
    public sealed class For<TRecipient, TMessage> : HubMessageHandlerDispatcher
        where TRecipient : class
        where TMessage : class
    {
        /// <summary>
        /// The underlying <see cref="HubMessageHandler{TRecipient, TMessage}"/> callback to invoke.
        /// </summary>
        private readonly HubMessageHandler<TRecipient, TMessage> handler;

        /// <summary>
        /// Initializes a new instance of the <see cref="For{TRecipient, TMessage}"/> class.
        /// </summary>
        /// <param name="handler">The input <see cref="HubMessageHandler{TRecipient, TMessage}"/> instance.</param>
        public For(HubMessageHandler<TRecipient, TMessage> handler)
        {
            this.handler = handler;
        }

        /// <inheritdoc/>
        public override void Invoke(object recipient, object message)
        {
            this.handler(Unsafe.As<TRecipient>(recipient), Unsafe.As<TMessage>(message));
        }
    }
}