using YFex.System.Unions;

namespace YFex.System.Data;

public record BrowserProfile(
    string BrowserName,
    string ProfileName,
    string ProfilePath
);

public record BrowsingHistoryEntry(
    string Url,
    string Title,
    int VisitCount,
    DateTimeOffset LastVisited
);

public record BookmarkEntry(
    string Title,
    string Url,
    string Folder
);

public record CookieDomain(
    string Domain,
    int CookieCount,
    DateTimeOffset? LatestExpiry
);

public record CategorizedVisit(
    string DomainHash,
    string Category,
    int VisitCount,
    DateTimeOffset LastVisited
);

public record BrowserSnapshot(
    BrowserProfile Profile,
    DataAvailability<IReadOnlyList<BrowsingHistoryEntry>> History,
    DataAvailability<IReadOnlyList<BookmarkEntry>> Bookmarks,
    DataAvailability<IReadOnlyList<CookieDomain>> CookieDomains
);
