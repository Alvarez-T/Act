using System.Buffers.Binary;
using System.Text;
using YFex.Persistence;

namespace YFex.Persistence.FileSystem;

/// <summary>
/// <see cref="IKeyValueStore"/> backed by the local file system. Each key maps to one file
/// inside <see cref="_basePath"/>; the key is URL-safe base64 encoded into the filename so it
/// round-trips (enabling prefix enumeration). Writes are atomic (temp + rename). An 8-byte
/// expiry header carries the optional TTL. Replaces the former <c>FileSystemSnapshotStore</c>.
/// </summary>
public sealed class FileSystemKeyValueStore : IKeyValueStore
{
    private const string Extension = ".kv";
    private readonly string _basePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <param name="basePath">
    /// Directory for entry files. Created on first write. Use a per-app subdirectory inside
    /// <c>Environment.GetFolderPath(SpecialFolder.LocalApplicationData)</c>.
    /// </param>
    public FileSystemKeyValueStore(string basePath) => _basePath = basePath;

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        string path = GetPath(key);
        if (!File.Exists(path)) return null;
        byte[] raw;
        try { raw = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false); }
        catch (IOException) { return null; }

        if (raw.Length < 8) return null;
        long expiryTicks = BinaryPrimitives.ReadInt64LittleEndian(raw);
        if (expiryTicks != 0 && expiryTicks < DateTime.UtcNow.Ticks)
        {
            TryDelete(path);
            return null;
        }
        return raw[8..];
    }

    public async ValueTask SetAsync(string key, byte[] value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        string path = GetPath(key);
        var buffer = new byte[8 + value.Length];
        long expiryTicks = ttl.HasValue ? (DateTime.UtcNow + ttl.Value).Ticks : 0;
        BinaryPrimitives.WriteInt64LittleEndian(buffer, expiryTicks);
        value.CopyTo(buffer, 8);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_basePath);
            string tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, buffer, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        finally { _writeLock.Release(); }
    }

    public ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        TryDelete(GetPath(key));
        return ValueTask.CompletedTask;
    }

    public async ValueTask<bool> ExistsAsync(string key, CancellationToken ct = default)
        => await GetAsync(key, ct).ConfigureAwait(false) is not null;

    public ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default)
    {
        List<string> keys = [];
        if (Directory.Exists(_basePath))
        {
            foreach (var file in Directory.EnumerateFiles(_basePath, "*" + Extension))
            {
                string? key = TryDecodeKey(Path.GetFileNameWithoutExtension(file));
                if (key is not null && key.StartsWith(prefix, StringComparison.Ordinal))
                    keys.Add(key);
            }
        }
        return new ValueTask<IReadOnlyList<string>>(keys);
    }

    public ValueTask ClearAsync(CancellationToken ct = default)
    {
        if (Directory.Exists(_basePath))
            foreach (var file in Directory.EnumerateFiles(_basePath, "*" + Extension))
                TryDelete(file);
        return ValueTask.CompletedTask;
    }

    private string GetPath(string key) => Path.Combine(_basePath, EncodeKey(key) + Extension);

    private static string EncodeKey(string key)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string? TryDecodeKey(string encoded)
    {
        try
        {
            string b64 = encoded.Replace('-', '+').Replace('_', '/');
            b64 = (b64.Length % 4) switch { 2 => b64 + "==", 3 => b64 + "=", _ => b64 };
            return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }
}
