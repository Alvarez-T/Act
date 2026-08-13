using YFex.System.Consent;

namespace YFex.System.Telemetry;

public interface ITelemetryConsent
{
    ConsentState CurrentState { get; }
    event Action<ConsentState>? ConsentChanged;
    event Action<ConsentLevel, ConsentLevel>? ConsentDowngraded;
    void UpdateConsent(ConsentState newState);
    bool IsAllowed(ConsentLevel required);

    static ConsentLevel GetLevel(ConsentState state) => state switch
    {
        FullConsent => ConsentLevel.Full,
        AnalyticsConsent => ConsentLevel.Analytics,
        FunctionalOnly => ConsentLevel.Functional,
        _ => ConsentLevel.None,
    };
}
