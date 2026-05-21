using MemoryPack;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Serialized container for an event travelling across a process boundary.
/// MemoryPack is used as the wire serializer (same as ActualLab.Rpc default).
/// </summary>
[MemoryPackable]
public partial record RpcEventEnvelope
{
    /// <summary>Assembly-qualified type name of the event payload.</summary>
    public string TypeName { get; init; } = "";

    /// <summary>MemoryPack-serialized payload bytes.</summary>
    public byte[] Payload { get; init; } = [];

    /// <summary>Optional target id for directed delivery. Null = broadcast.</summary>
    public string? TargetId { get; init; }

    /// <summary>Optional group id for group delivery. Null = broadcast or single-target.</summary>
    public string? GroupId { get; init; }
}
