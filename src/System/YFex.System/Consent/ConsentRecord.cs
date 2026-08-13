namespace YFex.System.Consent;

public record ConsentRecord(
    ConsentState State,
    DateTimeOffset RecordedAt,
    string PolicyVersion,
    bool BrowserDataEnabled,
    bool ActiveWindowTitleEnabled
);
