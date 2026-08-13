using YFex.System.Consent;

namespace YFex.System.Unions;

public record Available<T>(T Value, DateTimeOffset CollectedAt);

public record Denied(string Reason, ConsentLevel RequiredLevel);

public record Unsupported(string Platform, string Details);

public readonly union DataAvailability<T>(Available<T>, Denied, Unsupported);
