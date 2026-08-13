using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YFex.Persistence;

namespace YFex.System.Consent;

/// <summary>
/// <see cref="IConsentStore"/> that persists consent state as a JSON document through an
/// <see cref="ILocalStore"/>. The store owns file location and IO; this class owns only the
/// consent JSON schema.
/// </summary>
public sealed class FileConsentStore : IConsentStore
{
    /// <summary>Storage key for the consent document (used verbatim as the file name).</summary>
    public const string Key = "consent.json";

    private readonly ILocalStore _store;
    private ConsentState _cached;
    private bool _loaded;

    public FileConsentStore(ILocalStore store)
    {
        _store = store;
    }

    public bool HasBeenAsked => Load() is not NotAsked;

    public ConsentState Load()
    {
        if (_loaded) return _cached;

        var bytes = _store.Read(Key);
        if (bytes is null)
        {
            _cached = new NotAsked();
            _loaded = true;
            return _cached;
        }

        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;

            var type = root.GetProperty("type").GetString();
            _cached = type switch
            {
                "declined" => new Declined(
                    DateTimeOffset.Parse(root.GetProperty("timestamp").GetString()!)),
                "functional" => new FunctionalOnly(
                    DateTimeOffset.Parse(root.GetProperty("timestamp").GetString()!),
                    root.GetProperty("policyVersion").GetString()!),
                "analytics" => new AnalyticsConsent(
                    DateTimeOffset.Parse(root.GetProperty("timestamp").GetString()!),
                    root.GetProperty("policyVersion").GetString()!),
                "full" => new FullConsent(
                    DateTimeOffset.Parse(root.GetProperty("timestamp").GetString()!),
                    root.GetProperty("policyVersion").GetString()!),
                _ => new NotAsked()
            };
            _loaded = true;
            return _cached;
        }
        catch
        {
            _cached = new NotAsked();
            _loaded = true;
            return _cached;
        }
    }

    public void Save(ConsentState state)
    {
        _cached = state;
        _loaded = true;

        var obj = new JsonObject();
        switch (state)
        {
            case NotAsked:
                obj["type"] = "not_asked";
                break;
            case Declined d:
                obj["type"] = "declined";
                obj["timestamp"] = d.DeclinedAt.ToString("O");
                break;
            case FunctionalOnly f:
                obj["type"] = "functional";
                obj["timestamp"] = f.ConsentedAt.ToString("O");
                obj["policyVersion"] = f.PolicyVersion;
                break;
            case AnalyticsConsent a:
                obj["type"] = "analytics";
                obj["timestamp"] = a.ConsentedAt.ToString("O");
                obj["policyVersion"] = a.PolicyVersion;
                break;
            case FullConsent fc:
                obj["type"] = "full";
                obj["timestamp"] = fc.ConsentedAt.ToString("O");
                obj["policyVersion"] = fc.PolicyVersion;
                break;
        }

        _store.Write(Key, Encoding.UTF8.GetBytes(obj.ToJsonString()));
    }
}
