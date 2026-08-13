namespace YFex.System.Consent;

public interface IConsentStore
{
    ConsentState Load();
    void Save(ConsentState state);
    bool HasBeenAsked { get; }
}
