using MemoryPack;

namespace YFex.Persistence.Tests.TestObjects;

/// <summary>
/// Hand-written <see cref="IPersistableStateObject"/> that mirrors what
/// <c>YFex.Persistence.Generator</c> would emit for a class with
/// two <c>[Observable, Persist]</c> properties (Name: string, Count: int).
/// Used in provider tests without depending on the generator running in this project.
/// </summary>
public sealed class TestPersistableObject : IPersistableStateObject
{
    public string Name  { get; set; } = "";
    public int    Count { get; set; }

    public byte[] CaptureSnapshot()
    {
        byte[][] values =
        [
            MemoryPackSerializer.Serialize<string>(Name),
            MemoryPackSerializer.Serialize<int>(Count),
        ];
        return MemoryPackSerializer.Serialize(values);
    }

    public void RestoreSnapshot(ReadOnlySpan<byte> data)
    {
        var values = MemoryPackSerializer.Deserialize<byte[][]>(data);
        if (values is null) return;
        if (values.Length > 0) Name  = MemoryPackSerializer.Deserialize<string>(values[0]) ?? "";
        if (values.Length > 1) Count = MemoryPackSerializer.Deserialize<int>(values[1]);
    }
}
