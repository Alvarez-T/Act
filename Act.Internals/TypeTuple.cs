using System.Runtime.CompilerServices;

namespace Act.Utils;

//Source: https://github.com/CommunityToolkit/dotnet/blob/7b53ae23dfc6a7fb12d0fc058b89b6e948f48448/src/CommunityToolkit.Mvvm/Messaging/Internals/Type2.cs#L20
public readonly struct TypeTuple : IEquatable<TypeTuple>
{
    /// <summary>
    /// The type of registered message.
    /// </summary>
    public readonly Type TMessage;

    /// <summary>
    /// The type of registration token.
    /// </summary>
    public readonly Type TToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeTuple"/> struct.
    /// </summary>
    /// <param name="tMessage">The type of registered message.</param>
    /// <param name="tToken">The type of registration token.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TypeTuple(Type tMessage, Type tToken)
    {
        this.TMessage = tMessage;
        this.TToken = tToken;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(TypeTuple other)
    {
        // We can't just use reference equality, as that's technically not guaranteed
        // to work and might fail in very rare cases (eg. with type forwarding between
        // different assemblies). Instead, we can use the == operator to compare for
        // equality, which still avoids the callvirt overhead of calling Type.Equals,
        // and is also implemented as a JIT intrinsic on runtimes such as .NET Core.
        return
            this.TMessage == other.TMessage &&
            this.TToken == other.TToken;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is TypeTuple other && Equals(other);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        // To combine the two hashes, we can simply use the fast djb2 hash algorithm. Unfortunately we
        // can't really skip the callvirt here (eg. by using RuntimeHelpers.GetHashCode like in other
        // cases), as there are some niche cases mentioned above that might break when doing so.
        // However since this method is not generally used in a hot path (eg. the message broadcasting
        // only invokes this a handful of times when initially retrieving the target mapping), this
        // doesn't actually make a noticeable difference despite the minor overhead of the virtual call.
        int hash = this.TMessage.GetHashCode();

        hash = (hash << 5) + hash;

        hash += this.TToken.GetHashCode();

        return hash;
    }
}
