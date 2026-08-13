using YFex.System.Consent;

namespace YFex.System.Telemetry;

public sealed class TelemetryConsent : ITelemetryConsent
{
    private readonly IConsentStore _store;
    private ConsentState _current;

    public TelemetryConsent(IConsentStore store)
    {
        _store = store;
        _current = store.Load();
    }

    public ConsentState CurrentState => _current;

    public event Action<ConsentState>? ConsentChanged;
    public event Action<ConsentLevel, ConsentLevel>? ConsentDowngraded;

    public void UpdateConsent(ConsentState newState)
    {
        var oldLevel = ITelemetryConsent.GetLevel(_current);
        var newLevel = ITelemetryConsent.GetLevel(newState);

        _current = newState;
        _store.Save(newState);

        if (newLevel < oldLevel)
            ConsentDowngraded?.Invoke(oldLevel, newLevel);

        ConsentChanged?.Invoke(newState);
    }

    public bool IsAllowed(ConsentLevel required)
    {
        return (_current, required) switch
        {
            (FullConsent, _) => true,
            (AnalyticsConsent, ConsentLevel.Analytics or ConsentLevel.Functional or ConsentLevel.None) => true,
            (FunctionalOnly, ConsentLevel.Functional or ConsentLevel.None) => true,
            (_, ConsentLevel.None) => true,
            _ => false,
        };
    }
}
