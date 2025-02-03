using System.Runtime.CompilerServices;

namespace Act.Utils;

//Source: https://github.com/CommunityToolkit/dotnet/blob/7b53ae23dfc6a7fb12d0fc058b89b6e948f48448/src/CommunityToolkit.Mvvm/Messaging/Internals/Unit.cs#L13

/// <summary>
/// An empty type representing a generic token with no specific value.
/// </summary>
public readonly struct Unit : IEquatable<Unit>
{
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Unit other)
    {
        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is Unit;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        return 0;
    }
}