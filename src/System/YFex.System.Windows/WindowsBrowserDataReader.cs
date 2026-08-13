using System.Text.Json;
using Microsoft.Data.Sqlite;
using YFex.System.Consent;
using YFex.System.Data;
using YFex.System.Platform;
using YFex.System.Telemetry;
using YFex.System.Unions;

namespace YFex.System.Windows;

public sealed class WindowsBrowserDataReader : IBrowserDataReader
{
    private readonly ITelemetryConsent _consent;

    private static readonly long ChromeEpochTicks =
        new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;

    public WindowsBrowserDataReader(ITelemetryConsent consent)
    {
        _consent = consent;
    }

    public DataAvailability<IReadOnlyList<BrowserProfile>> DetectProfiles()
    {
        if (!_consent.IsAllowed(ConsentLevel.Full))
            return new Denied("Requires full consent with browser data enabled", ConsentLevel.Full);

        var profiles = new List<BrowserProfile>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        DetectChromiumProfiles(profiles, localAppData, @"Google\Chrome\User Data", "Chrome");
        DetectChromiumProfiles(profiles, localAppData, @"Microsoft\Edge\User Data", "Edge");
        DetectChromiumProfiles(profiles, localAppData, @"BraveSoftware\Brave-Browser\User Data", "Brave");
        DetectChromiumProfiles(profiles, localAppData, @"Vivaldi\User Data", "Vivaldi");
        DetectChromiumProfiles(profiles, localAppData, @"Opera Software\Opera Stable", "Opera");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        DetectFirefoxProfiles(profiles, appData);

        return new Available<IReadOnlyList<BrowserProfile>>(profiles, DateTimeOffset.UtcNow);
    }

    public DataAvailability<BrowserSnapshot> ReadProfile(BrowserProfile profile)
    {
        if (!_consent.IsAllowed(ConsentLevel.Full))
            return new Denied("Requires full consent with browser data enabled", ConsentLevel.Full);

        bool isFirefox = profile.BrowserName.Equals("Firefox", StringComparison.OrdinalIgnoreCase);

        DataAvailability<IReadOnlyList<BrowsingHistoryEntry>> history =
            isFirefox ? ReadFirefoxHistory(profile.ProfilePath) : ReadChromiumHistory(profile.ProfilePath);

        DataAvailability<IReadOnlyList<BookmarkEntry>> bookmarks =
            isFirefox ? ReadFirefoxBookmarks(profile.ProfilePath) : ReadChromiumBookmarks(profile.ProfilePath);

        DataAvailability<IReadOnlyList<CookieDomain>> cookies =
            isFirefox ? ReadFirefoxCookies(profile.ProfilePath) : ReadChromiumCookies(profile.ProfilePath);

        var snapshot = new BrowserSnapshot(profile, history, bookmarks, cookies);
        return new Available<BrowserSnapshot>(snapshot, DateTimeOffset.UtcNow);
    }

    // --- Chromium History ---

    private static DataAvailability<IReadOnlyList<BrowsingHistoryEntry>> ReadChromiumHistory(string profilePath)
    {
        var dbPath = Path.Combine(profilePath, "History");
        using var db = BrowserDb.Open(dbPath);
        if (db is null)
            return new Unsupported("Windows", "History database not accessible");

        var entries = new List<BrowsingHistoryEntry>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT url, title, visit_count, last_visit_time FROM urls ORDER BY last_visit_time DESC LIMIT 1000";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new BrowsingHistoryEntry(
                reader.GetString(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.GetInt32(2),
                FromChromeTimestamp(reader.GetInt64(3))));
        }

        return new Available<IReadOnlyList<BrowsingHistoryEntry>>(entries, DateTimeOffset.UtcNow);
    }

    // --- Chromium Bookmarks (JSON file) ---

    private static DataAvailability<IReadOnlyList<BookmarkEntry>> ReadChromiumBookmarks(string profilePath)
    {
        var bookmarksFile = Path.Combine(profilePath, "Bookmarks");
        if (!File.Exists(bookmarksFile))
            return new Unsupported("Windows", "Bookmarks file not found");

        try
        {
            var json = File.ReadAllText(bookmarksFile);
            using var doc = JsonDocument.Parse(json);
            var bookmarks = new List<BookmarkEntry>();

            if (doc.RootElement.TryGetProperty("roots", out var roots))
            {
                foreach (var rootProp in roots.EnumerateObject())
                    TraverseBookmarkNode(rootProp.Value, rootProp.Name, bookmarks);
            }

            return new Available<IReadOnlyList<BookmarkEntry>>(bookmarks, DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            return new Unsupported("Windows", "Failed to parse bookmarks file");
        }
    }

    private static void TraverseBookmarkNode(JsonElement node, string folder, List<BookmarkEntry> bookmarks)
    {
        if (!node.TryGetProperty("type", out var typeProp)) return;

        var type = typeProp.GetString();
        if (type == "url")
        {
            var name = node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url = node.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            bookmarks.Add(new BookmarkEntry(name, url, folder));
        }
        else if (type == "folder" && node.TryGetProperty("children", out var children))
        {
            var folderName = node.TryGetProperty("name", out var n) ? n.GetString() ?? folder : folder;
            foreach (var child in children.EnumerateArray())
                TraverseBookmarkNode(child, folderName, bookmarks);
        }
    }

    // --- Chromium Cookies ---

    private static DataAvailability<IReadOnlyList<CookieDomain>> ReadChromiumCookies(string profilePath)
    {
        var dbPath = Path.Combine(profilePath, "Network", "Cookies");
        if (!File.Exists(dbPath))
            dbPath = Path.Combine(profilePath, "Cookies");

        using var db = BrowserDb.Open(dbPath);
        if (db is null)
            return new Unsupported("Windows", "Cookies database not accessible");

        var domains = new List<CookieDomain>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT host_key, COUNT(*) as cnt, MAX(expires_utc) as max_exp FROM cookies GROUP BY host_key ORDER BY cnt DESC LIMIT 500";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var host = reader.GetString(0);
            int count = reader.GetInt32(1);
            DateTimeOffset? expiry = reader.IsDBNull(2) ? null : FromChromeTimestamp(reader.GetInt64(2));
            domains.Add(new CookieDomain(host, count, expiry));
        }

        return new Available<IReadOnlyList<CookieDomain>>(domains, DateTimeOffset.UtcNow);
    }

    // --- Firefox History ---

    private static DataAvailability<IReadOnlyList<BrowsingHistoryEntry>> ReadFirefoxHistory(string profilePath)
    {
        var dbPath = Path.Combine(profilePath, "places.sqlite");
        using var db = BrowserDb.Open(dbPath);
        if (db is null)
            return new Unsupported("Windows", "Firefox places database not accessible");

        var entries = new List<BrowsingHistoryEntry>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT p.url, p.title, p.visit_count, p.last_visit_date
            FROM moz_places p
            WHERE p.visit_count > 0
            ORDER BY p.last_visit_date DESC
            LIMIT 1000
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new BrowsingHistoryEntry(
                reader.GetString(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? DateTimeOffset.MinValue : FromFirefoxTimestamp(reader.GetInt64(3))));
        }

        return new Available<IReadOnlyList<BrowsingHistoryEntry>>(entries, DateTimeOffset.UtcNow);
    }

    // --- Firefox Bookmarks (from places.sqlite) ---

    private static DataAvailability<IReadOnlyList<BookmarkEntry>> ReadFirefoxBookmarks(string profilePath)
    {
        var dbPath = Path.Combine(profilePath, "places.sqlite");
        using var db = BrowserDb.Open(dbPath);
        if (db is null)
            return new Unsupported("Windows", "Firefox places database not accessible");

        var bookmarks = new List<BookmarkEntry>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT b.title, p.url, parent_b.title as folder
            FROM moz_bookmarks b
            JOIN moz_places p ON b.fk = p.id
            LEFT JOIN moz_bookmarks parent_b ON b.parent = parent_b.id
            WHERE b.type = 1 AND p.url NOT LIKE 'place:%'
            LIMIT 1000
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            bookmarks.Add(new BookmarkEntry(
                reader.IsDBNull(0) ? "" : reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2)));
        }

        return new Available<IReadOnlyList<BookmarkEntry>>(bookmarks, DateTimeOffset.UtcNow);
    }

    // --- Firefox Cookies ---

    private static DataAvailability<IReadOnlyList<CookieDomain>> ReadFirefoxCookies(string profilePath)
    {
        var dbPath = Path.Combine(profilePath, "cookies.sqlite");
        using var db = BrowserDb.Open(dbPath);
        if (db is null)
            return new Unsupported("Windows", "Firefox cookies database not accessible");

        var domains = new List<CookieDomain>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT host, COUNT(*) as cnt, MAX(expiry) as max_exp FROM moz_cookies GROUP BY host ORDER BY cnt DESC LIMIT 500";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var host = reader.GetString(0);
            int count = reader.GetInt32(1);
            DateTimeOffset? expiry = reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2));
            domains.Add(new CookieDomain(host, count, expiry));
        }

        return new Available<IReadOnlyList<CookieDomain>>(domains, DateTimeOffset.UtcNow);
    }

    // --- Profile Detection ---

    private static void DetectChromiumProfiles(
        List<BrowserProfile> profiles,
        string localAppData,
        string relativePath,
        string browserName)
    {
        var userData = Path.Combine(localAppData, relativePath);
        if (!Directory.Exists(userData)) return;

        var defaultPath = Path.Combine(userData, "Default");
        if (Directory.Exists(defaultPath))
            profiles.Add(new BrowserProfile(browserName, "Default", defaultPath));

        try
        {
            foreach (var dir in Directory.GetDirectories(userData, "Profile *"))
                profiles.Add(new BrowserProfile(browserName, Path.GetFileName(dir), dir));
        }
        catch { }
    }

    private static void DetectFirefoxProfiles(List<BrowserProfile> profiles, string appData)
    {
        var ffPath = Path.Combine(appData, @"Mozilla\Firefox\Profiles");
        if (!Directory.Exists(ffPath)) return;

        try
        {
            foreach (var dir in Directory.GetDirectories(ffPath))
            {
                if (File.Exists(Path.Combine(dir, "places.sqlite")))
                    profiles.Add(new BrowserProfile("Firefox", Path.GetFileName(dir), dir));
            }
        }
        catch { }
    }

    // --- Timestamp Conversion ---

    private static DateTimeOffset FromChromeTimestamp(long microseconds)
    {
        if (microseconds <= 0) return DateTimeOffset.MinValue;
        try
        {
            var ticks = ChromeEpochTicks + microseconds * 10;
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        catch { return DateTimeOffset.MinValue; }
    }

    private static DateTimeOffset FromFirefoxTimestamp(long microseconds)
    {
        if (microseconds <= 0) return DateTimeOffset.MinValue;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(microseconds / 1000); }
        catch { return DateTimeOffset.MinValue; }
    }

    // --- SQLite Helper ---

    private sealed class BrowserDb : IDisposable
    {
        public SqliteConnection Connection { get; }
        private readonly string? _tempPath;

        public static BrowserDb? Open(string dbPath)
        {
            if (!File.Exists(dbPath)) return null;

            try
            {
                var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                conn.Open();
                return new BrowserDb(conn, null);
            }
            catch
            {
                try
                {
                    var tempPath = Path.Combine(Path.GetTempPath(), $"yfex_{Guid.NewGuid():N}.db");
                    File.Copy(dbPath, tempPath, true);

                    var walPath = dbPath + "-wal";
                    if (File.Exists(walPath))
                        File.Copy(walPath, tempPath + "-wal", true);

                    var conn = new SqliteConnection($"Data Source={tempPath};Mode=ReadOnly");
                    conn.Open();
                    return new BrowserDb(conn, tempPath);
                }
                catch { return null; }
            }
        }

        private BrowserDb(SqliteConnection connection, string? tempPath)
        {
            Connection = connection;
            _tempPath = tempPath;
        }

        public void Dispose()
        {
            Connection.Dispose();
            if (_tempPath is null) return;
            try { File.Delete(_tempPath); } catch { }
            try { File.Delete(_tempPath + "-wal"); } catch { }
            try { File.Delete(_tempPath + "-shm"); } catch { }
        }
    }
}
