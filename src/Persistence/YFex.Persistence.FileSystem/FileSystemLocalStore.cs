using YFex.Persistence;

namespace YFex.Persistence.FileSystem;

/// <summary>
/// <see cref="ISyncLocalStore"/> backed by the local file system. Each key maps to a
/// single file inside <see cref="_basePath"/>. The key is used verbatim as the file name
/// (after sanitization), so callers can preserve a specific extension such as
/// <c>consent.json</c>.
/// </summary>
public sealed class FileSystemLocalStore : ISyncLocalStore
{
    private readonly string _basePath;

    /// <param name="basePath">
    /// Directory where document files are stored. Created on first write if it doesn't exist.
    /// Use a per-app subdirectory inside <c>Environment.GetFolderPath(SpecialFolder.LocalApplicationData)</c>.
    /// </param>
    public FileSystemLocalStore(string basePath)
    {
        _basePath = basePath;
    }

    public byte[]? Read(string key)
    {
        string path = GetPath(key);
        if (!File.Exists(path)) return null;
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Write(string key, byte[] data)
    {
        string path = GetPath(key);
        Directory.CreateDirectory(_basePath);
        // Write to temp then rename — atomic on most file systems
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    public void Delete(string key)
    {
        string path = GetPath(key);
        if (File.Exists(path)) File.Delete(path);
    }

    public bool Exists(string key) => File.Exists(GetPath(key));

    // Sanitize key into a safe filename, preserving any extension the caller supplied.
    private string GetPath(string key)
    {
        string safe = string.Join("_", key.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_basePath, safe);
    }
}
