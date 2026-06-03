namespace YFex.Messaging.Rpc.Sqlite;

public sealed class SqliteStorageOptions
{
    public string DatabaseFileName { get; set; } = "yfex-client-cache.db";

    /// <summary>
    /// Override the directory where the .db file is placed.
    /// Defaults to <c>%LOCALAPPDATA%/YFex</c> on Windows, <c>~/.local/share/YFex</c> on Linux/macOS.
    /// </summary>
    public string? Directory { get; set; }

    internal string ResolveDbPath()
    {
        var dir = Directory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YFex");
        System.IO.Directory.CreateDirectory(dir);
        return System.IO.Path.Combine(dir, DatabaseFileName);
    }
}
