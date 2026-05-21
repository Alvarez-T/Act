using YFex.Persistence;

namespace YFex.Persistence.FileSystem;

/// <summary>
/// <see cref="ISnapshotStore"/> backed by the local file system.
/// Each key maps to a single file inside <see cref="BasePath"/>.
/// Safe for desktop and server scenarios; not suitable for Blazor WASM.
/// </summary>
public sealed class FileSystemSnapshotStore : ISnapshotStore
{
    private readonly string _basePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <param name="basePath">
    /// Directory where snapshot files are stored. Created on first save if it doesn't exist.
    /// Use a per-app subdirectory inside <c>Environment.GetFolderPath(SpecialFolder.LocalApplicationData)</c>.
    /// </param>
    public FileSystemSnapshotStore(string basePath)
    {
        _basePath = basePath;
    }

    public async Task SaveAsync(string key, byte[] data, CancellationToken ct = default)
    {
        string path = GetPath(key);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_basePath);
            // Write to temp then rename — atomic on most file systems
            string tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<byte[]?> LoadAsync(string key, CancellationToken ct = default)
    {
        string path = GetPath(key);
        if (!File.Exists(path)) return null;
        try
        {
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        string path = GetPath(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    // Sanitize key into a safe filename: replace path-unsafe chars with underscores
    private string GetPath(string key)
    {
        string safe = string.Join("_", key.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_basePath, safe + ".snap");
    }
}
