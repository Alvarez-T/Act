namespace YFex.Consent;

/// <summary>
/// Captures the user's consent decision together with the per-category opt-ins that gate the most
/// sensitive profiling sources. Category flags only take effect at the consent level that unlocks
/// their source (see the provider consent tiers); a flag left <c>false</c> keeps that source off
/// even under Full consent.
/// </summary>
public record ConsentRecord(
    ConsentState State,
    DateTimeOffset RecordedAt,
    string PolicyVersion,
    // Existing sensitive-source opt-ins.
    bool BrowserDataEnabled = false,
    bool ActiveWindowTitleEnabled = false,
    // Additional opt-ins for OS/security profiling sources (map 1:1 to capture toggles).
    bool NetworkCaptureEnabled = false,
    bool ProcessDetailEnabled = false,
    bool FileActivityEnabled = false,
    bool TrafficInterceptionEnabled = false
);
