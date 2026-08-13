namespace YFex.System.Consent;

public record NotAsked;

public record Declined(DateTimeOffset DeclinedAt);

public record FunctionalOnly(DateTimeOffset ConsentedAt, string PolicyVersion);

public record AnalyticsConsent(DateTimeOffset ConsentedAt, string PolicyVersion);

public record FullConsent(DateTimeOffset ConsentedAt, string PolicyVersion);

public union ConsentState(NotAsked, Declined, FunctionalOnly, AnalyticsConsent, FullConsent);
