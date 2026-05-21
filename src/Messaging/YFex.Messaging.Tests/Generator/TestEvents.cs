namespace YFex.Messaging.Tests.Generator;

// ── Test event records ─────────────────────────────────────────────────────

/// <summary>Simple broadcast event for testing basic subscription wiring.</summary>
public record CounterEvent(int Value);

/// <summary>
/// Event with a MatchId property used to test FilterBy guard emission.
/// FilterBy = "MatchId" on the VM causes the generator to emit:
/// <c>if (!object.Equals(e.MatchId, this.MatchId)) return;</c>
/// </summary>
public record FilteredEvent(int MatchId, string Payload);

/// <summary>Async variant — handler returns ValueTask.</summary>
public record AsyncEvent(string Message);

/// <summary>Struct event used to verify zero-copy semantics.</summary>
public readonly record struct StructEvent(int X, int Y);
