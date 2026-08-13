using System.Security.Cryptography;
using System.Text;

namespace YFex.System.Telemetry;

public static class PrivacyScrubber
{
    public static TelemetryEvent Scrub(TelemetryEvent evt, string salt)
    {
        var scrubbed = new Dictionary<string, object?>(evt.Properties.Count);
        foreach (var (key, value) in evt.Properties)
        {
            var lowerKey = key.ToLowerInvariant();
            scrubbed[key] = lowerKey switch
            {
                "url" or "uri" when value is string url => HashUrl(url, salt),
                _ when lowerKey.Contains("title") && value is string s => HashString(s, salt),
                _ when lowerKey.Contains("mac") && value is string s => HashString(s, salt),
                _ when (lowerKey.Contains("ip") && !lowerKey.Contains("script") && !lowerKey.Contains("zip")) && value is string s
                    => MaskIpAddress(s),
                _ => value
            };
        }
        return evt with { Properties = scrubbed };
    }

    public static string HashUrl(string url, string salt)
    {
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            return HashString(host, salt);
        }
        catch
        {
            return "invalid_url";
        }
    }

    public static string HashString(string value, string salt)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value + salt));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string MaskIpAddress(string ip)
    {
        if (ip.Contains('.'))
        {
            var parts = ip.Split('.');
            if (parts.Length == 4)
                return $"{parts[0]}.{parts[1]}.{parts[2]}.xxx";
        }
        if (ip.Contains(':'))
        {
            var idx = ip.LastIndexOf(':');
            if (idx > 0) return ip[..idx] + ":xxxx";
        }
        return "masked";
    }
}
