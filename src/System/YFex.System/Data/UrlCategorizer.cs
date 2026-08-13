using System.Security.Cryptography;
using System.Text;

namespace YFex.System.Data;

public static class UrlCategorizer
{
    public static string Categorize(string url)
    {
        var host = new Uri(url).Host.ToLowerInvariant();
        return host switch
        {
            _ when host.Contains("github") || host.Contains("stackoverflow")
                || host.Contains("npmjs") || host.Contains("nuget")
                || host.Contains("gitlab") || host.Contains("bitbucket")
                => "developer",
            _ when host.Contains("amazon") || host.Contains("ebay")
                || host.Contains("shopify") || host.Contains("mercadolivre")
                || host.Contains("aliexpress")
                => "shopping",
            _ when host.Contains("facebook") || host.Contains("instagram")
                || host.Contains("twitter") || host.Contains("tiktok")
                || host.Contains("linkedin") || host.Contains("reddit")
                => "social",
            _ when host.Contains("youtube") || host.Contains("netflix")
                || host.Contains("spotify") || host.Contains("twitch")
                || host.Contains("disney")
                => "entertainment",
            _ when host.Contains("cnn") || host.Contains("bbc")
                || host.Contains("reuters") || host.Contains("g1.globo")
                || host.Contains("folha") || host.Contains("uol")
                => "news",
            _ when host.Contains("google") || host.Contains("bing")
                || host.Contains("duckduckgo")
                => "search",
            _ when host.Contains("bank") || host.Contains("paypal")
                || host.Contains("stripe")
                => "finance",
            _ => "other"
        };
    }

    public static CategorizedVisit CategorizeAndHash(BrowsingHistoryEntry entry, string salt)
    {
        var host = new Uri(entry.Url).Host.ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(host + salt));

        return new CategorizedVisit(
            Convert.ToHexString(hash).ToLowerInvariant(),
            Categorize(entry.Url),
            entry.VisitCount,
            entry.LastVisited
        );
    }
}
