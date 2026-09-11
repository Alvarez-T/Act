using System.Text.Json.Serialization;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the library's own persistence DTOs
/// (<see cref="StorageBackedOutbox.OutboxEntryDto"/>, <see cref="SyncFailure"/>). Using the generated
/// <c>JsonTypeInfo&lt;T&gt;</c> here keeps outbox / failure-log storage serialization fully AOT / trim
/// safe — no reflection-based <see cref="System.Text.Json.JsonSerializer"/>, no IL2026 / IL3050.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(StorageBackedOutbox.OutboxEntryDto))]
[JsonSerializable(typeof(SyncFailure))]
internal sealed partial class RpcInternalJsonContext : JsonSerializerContext;
