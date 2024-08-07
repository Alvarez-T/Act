using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Act.Internals;
using Act.Internals.Exceptions;
using CommunityToolkit.Mvvm.Messaging;

namespace Act.SignalR.Client;

public abstract class HubClient
{
    private readonly SpecializedDictionary<TypeTuple, SpecializedConditionalWeakTable<object, object?>> recipientsMap = new();

    /// <inheritdoc/>
    internal void Register<TRecipient, TMessage, TToken>(TRecipient recipient, TToken token, HubMessageHandler<TRecipient, TMessage> handler)
        where TRecipient : class
        where TMessage : class
        where TToken : IEquatable<TToken>
    {
        Internals.Exceptions.ArgumentNullException.ThrowIfNull(recipient);
        Internals.Exceptions.ArgumentNullException.For<TToken>.ThrowIfNull(token);
        Internals.Exceptions.ArgumentNullException.ThrowIfNull(handler);

        Register<TMessage, TToken>(recipient, token, new HubMessageHandlerDispatcher.For<TRecipient, TMessage>(handler));
    }

    /// <summary>
    /// Registers a recipient for a given type of message.
    /// </summary>
    /// <typeparam name="TMessage">The type of message to receive.</typeparam>
    /// <typeparam name="TToken">The type of token to use to pick the messages to receive.</typeparam>
    /// <param name="recipient">The recipient that will receive the messages.</param>
    /// <param name="token">A token used to determine the receiving channel to use.</param>
    /// <exception cref="InvalidOperationException">Thrown when trying to register the same message twice.</exception>
    /// <remarks>
    /// This method is a variation of <see cref="Register{TRecipient, TMessage, TToken}(TRecipient, TToken, MessageHandler{TRecipient, TMessage})"/>
    /// that is specialized for recipients implementing <see cref="IRecipient{TMessage}"/>. See more comments at the top of this type, as well as
    /// within <see cref="Send{TMessage, TToken}(TMessage, TToken)"/> and in the <see cref="MessageHandlerDispatcher"/> types.
    /// </remarks>
    internal void Register<TMessage, TToken>(IRecipient<TMessage> recipient, TToken token)
        where TMessage : class
        where TToken : IEquatable<TToken>
    {
        Register<TMessage, TToken>(recipient, token, null);
    }

    /// <summary>
    /// Registers a recipient for a given type of message.
    /// </summary>
    /// <typeparam name="TMessage">The type of message to receive.</typeparam>
    /// <typeparam name="TToken">The type of token to use to pick the messages to receive.</typeparam>
    /// <param name="recipient">The recipient that will receive the messages.</param>
    /// <param name="token">A token used to determine the receiving channel to use.</param>
    /// <param name="dispatcher">The input <see cref="MessageHandlerDispatcher"/> instance to register, or null.</param>
    /// <exception cref="InvalidOperationException">Thrown when trying to register the same message twice.</exception>
    private void Register<TMessage, TToken>(object recipient, TToken token, HubMessageHandlerDispatcher? dispatcher)
        where TMessage : class
        where TToken : IEquatable<TToken>
    {
        lock (this.recipientsMap)
        {
            TypeTuple type2 = new(typeof(TMessage), typeof(TToken));

            // Get the conditional table for the pair of type arguments, or create it if it doesn't exist
            ref SpecializedConditionalWeakTable<object, object?>? mapping = ref this.recipientsMap.GetOrAddValueRef(type2);

            mapping ??= new SpecializedConditionalWeakTable<object, object?>();

            // Fast path for unit tokens
            if (typeof(TToken) == typeof(Unit))
            {
                if (!mapping.TryAdd(recipient, dispatcher))
                {
                    ThrowInvalidOperationExceptionForDuplicateRegistration();
                }
            }
            else
            {
                // Get or create the handlers dictionary for the target recipient
                SpecializedDictionary<TToken, object?>? map = Unsafe.As<SpecializedDictionary<TToken, object?>>(mapping.GetValue(recipient, static _ => new SpecializedDictionary<TToken, object?>())!);

                // Add the new registration entry
                ref object? registeredHandler = ref map.GetOrAddValueRef(token);

                if (registeredHandler is not null)
                {
                    ThrowInvalidOperationExceptionForDuplicateRegistration();
                }

                // Store the input handler
                registeredHandler = dispatcher;
            }
        }
    }

    protected TMessage Send<TMessage, TToken>(TMessage message, TToken token)
        where TMessage : class
        where TToken : IEquatable<TToken>
    {
        Internals.Exceptions.ArgumentNullException.ThrowIfNull(message);
        Internals.Exceptions.ArgumentNullException.For<TToken>.ThrowIfNull(token);

        RefArrayPoolBufferWriter<object?> bufferWriter;
        int i = 0;

        lock (this.recipientsMap)
        {
            TypeTuple typeTuple = new(typeof(TMessage), typeof(TToken));

            // Try to get the target table
            if (!this.recipientsMap.TryGetValue(typeTuple, out SpecializedConditionalWeakTable<object, object?>? table))
            {
                return message;
            }

            bufferWriter = RefArrayPoolBufferWriter<object?>.Create();

            // We need a local, temporary copy of all the pending recipients and handlers to
            // invoke, to avoid issues with handlers unregistering from messages while we're
            // holding the lock. To do this, we can just traverse the conditional table in use
            // to enumerate all the existing recipients for the token and message types pair
            // corresponding to the generic arguments for this invocation, and then track the
            // handlers with a matching token, and their corresponding recipients.
            using SpecializedConditionalWeakTable<object, object?>.Enumerator enumerator = table.GetEnumerator();

            while (enumerator.MoveNext())
            {
                if (typeof(TToken) == typeof(Unit))
                {
                    bufferWriter.Add(enumerator.GetValue());
                    bufferWriter.Add(enumerator.GetKey());
                    i++;
                }
                else
                {
                    SpecializedDictionary<TToken, object?>? map = Unsafe.As<SpecializedDictionary<TToken, object?>>(enumerator.GetValue()!);

                    if (map.TryGetValue(token, out object? handler))
                    {
                        bufferWriter.Add(handler);
                        bufferWriter.Add(enumerator.GetKey());
                        i++;
                    }
                }
            }
        }

        try
        {
            SendAll(bufferWriter.Span, i, message);
        }
        finally
        {
            bufferWriter.Dispose();
        }

        return message;
    }

    /// <summary>
    /// Implements the broadcasting logic for <see cref="Send{TMessage, TToken}(TMessage, TToken)"/>.
    /// </summary>
    /// <typeparam name="TMessage"></typeparam>
    /// <param name="pairs"></param>
    /// <param name="i"></param>
    /// <param name="message"></param>
    /// <remarks>
    /// This method is not a local function to avoid triggering multiple compilations due to <c>TToken</c>
    /// potentially being a value type, which results in specialized code due to reified generics. This is
    /// necessary to work around a Roslyn limitation that causes unnecessary type parameters in local
    /// functions not to be discarded in the synthesized methods. Additionally, keeping this loop outside
    /// of the EH block (the <see langword="try"/> block) can help result in slightly better codegen.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void SendAll<TMessage>(ReadOnlySpan<object?> pairs, int i, TMessage message)
        where TMessage : class
    {
        // This Slice calls executes bounds checks for the loop below, in case i was somehow wrong.
        // The rest of the implementation relies on bounds checks removal and loop strength reduction
        // done manually (which results in a 20% speedup during broadcast), since the JIT is not able
        // to recognize this pattern. Skipping checks below is a provably safe optimization: the slice
        // has exactly 2 * i elements (due to this slicing), and each loop iteration processes a pair.
        // The loops ends when the initial reference reaches the end, and that's incremented by 2 at
        // the end of each iteration. The target being a span, obviously means the length is constant.
        ReadOnlySpan<object?> slice = pairs.Slice(0, 2 * i);

        ref object? sliceStart = ref MemoryMarshal.GetReference(slice);
        ref object? sliceEnd = ref Unsafe.Add(ref sliceStart, slice.Length);

        while (Unsafe.IsAddressLessThan(ref sliceStart, ref sliceEnd))
        {
            object? handler = sliceStart;
            object recipient = Unsafe.Add(ref sliceStart, 1)!;

            // Here we need to distinguish the two possible cases: either the recipient was registered
            // through the IRecipient<TMessage> interface, or with a custom handler. In the first case,
            // the handler stored in the messenger is just null, so we can check that and branch to a
            // fast path that just invokes IRecipient<TMessage> directly on the recipient. Otherwise,
            // we will use the standard double dispatch approach. This check is particularly convenient
            // as we only need to check for null to determine what registration type was used, without
            // having to store any additional info in the messenger. This will produce code as follows,
            // with the advantage of also being compact and not having to use any additional registers:
            // =============================
            // L0000: test rcx, rcx
            // L0003: jne short L0040
            // =============================
            // Which is extremely fast. The reason for this conditional check in the first place is that
            // we're doing manual (null based) guarded devirtualization: if the handler is the marker
            // type and not an actual handler then we know that the recipient implements
            // IRecipient<TMessage>, so we can just cast to it and invoke it directly. This avoids
            // having to store the proxy callback when registering, and also skips an indirection
            // (invoking the delegate that then invokes the actual method). Additional note: this
            // pattern ensures that both casts below do not actually alias incompatible reference
            // types (as in, they would both succeed if they were safe casts), which lets the code
            // not rely on undefined behavior to run correctly (ie. we're not aliasing delegates).
            if (handler is null)
            {
                Unsafe.As<IRecipient<TMessage>>(recipient).Receive(message);
            }
            else
            {
                Unsafe.As<HubMessageHandlerDispatcher>(handler).Invoke(recipient, message);
            }

            sliceStart = ref Unsafe.Add(ref sliceStart, 2);
        }
    }

    private static void ThrowInvalidOperationExceptionForDuplicateRegistration()
    {
        throw new InvalidOperationException("The target recipient has already subscribed to the target message.");
    }
}