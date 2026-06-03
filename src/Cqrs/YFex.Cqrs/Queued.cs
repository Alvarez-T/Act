namespace YFex.Cqrs;

/// <summary>Represents a command that has been accepted into the outbox queue for later execution.</summary>
public readonly record struct Queued(Guid IdempotencyKey);
