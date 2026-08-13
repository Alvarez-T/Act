using Microsoft.JSInterop;
using YFex.Persistence;

namespace YFex.Persistence.IndexedDb;

/// <summary>
/// <see cref="IKeyValueStore"/> backed by the browser's IndexedDB API via JS interop.
/// Safe only inside a Blazor WASM context; on SSR/non-browser it either throws or falls back
/// to <see cref="MemoryKeyValueStore"/> per <see cref="IndexedDbStorageOptions.UnavailableAction"/>.
/// Formerly <c>IndexedDBClientStorage</c>.
/// </summary>
public sealed class IndexedDbKeyValueStore : IKeyValueStore, IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly IndexedDbStorageOptions _opts;
    private IJSObjectReference? _module;
    private IKeyValueStore? _fallback;
    private bool _unavailable;

    public IndexedDbKeyValueStore(IJSRuntime js, IndexedDbStorageOptions opts)
    {
        _js = js;
        _opts = opts;
    }

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        var module = await GetModuleAsync(ct).ConfigureAwait(false);
        if (module is null) return await _fallback!.GetAsync(key, ct).ConfigureAwait(false);
        return await module.InvokeAsync<byte[]?>("yfexIdbGet", ct, key).ConfigureAwait(false);
    }

    public async ValueTask SetAsync(string key, byte[] value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var module = await GetModuleAsync(ct).ConfigureAwait(false);
        if (module is null) { await _fallback!.SetAsync(key, value, ttl, ct).ConfigureAwait(false); return; }
        long expiresAtMs = ttl.HasValue ? DateTimeOffset.UtcNow.Add(ttl.Value).ToUnixTimeMilliseconds() : 0L;
        await module.InvokeVoidAsync("yfexIdbSet", ct, key, value, expiresAtMs).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        var module = await GetModuleAsync(ct).ConfigureAwait(false);
        if (module is null) { await _fallback!.DeleteAsync(key, ct).ConfigureAwait(false); return; }
        await module.InvokeVoidAsync("yfexIdbDelete", ct, key).ConfigureAwait(false);
    }

    public async ValueTask<bool> ExistsAsync(string key, CancellationToken ct = default)
        => await GetAsync(key, ct).ConfigureAwait(false) is not null;

    public async ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default)
    {
        var module = await GetModuleAsync(ct).ConfigureAwait(false);
        if (module is null) return await _fallback!.GetKeysWithPrefixAsync(prefix, ct).ConfigureAwait(false);
        return await module.InvokeAsync<string[]>("yfexIdbGetKeysWithPrefix", ct, prefix).ConfigureAwait(false);
    }

    public async ValueTask ClearAsync(CancellationToken ct = default)
    {
        var module = await GetModuleAsync(ct).ConfigureAwait(false);
        if (module is null) { await _fallback!.ClearAsync(ct).ConfigureAwait(false); return; }
        await module.InvokeVoidAsync("yfexIdbClear", ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
            await _module.DisposeAsync().ConfigureAwait(false);
    }

    // ── module loading ───────────────────────────────────────────────────────

    private static readonly SemaphoreSlim _loadLock = new(1, 1);

    private async ValueTask<IJSObjectReference?> GetModuleAsync(CancellationToken ct)
    {
        if (_unavailable) return null;
        if (_module is not null) return _module;

        await _loadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_module is not null) return _module;
            _module = await _js.InvokeAsync<IJSObjectReference>(
                "import", ct, "./_content/YFex.Persistence.IndexedDb/yfex-idb.js").ConfigureAwait(false);
            return _module;
        }
        catch (Exception) when (!OperatingSystem.IsBrowser())
        {
            return HandleUnavailable();
        }
        catch (JSException ex) when (ex.Message.Contains("QuotaExceeded", StringComparison.OrdinalIgnoreCase)
                                  || ex.Message.Contains("NotAvailable", StringComparison.OrdinalIgnoreCase))
        {
            return HandleUnavailable();
        }
        finally { _loadLock.Release(); }
    }

    private IJSObjectReference? HandleUnavailable()
    {
        _unavailable = true;
        if (_opts.UnavailableAction == IndexedDbUnavailableAction.Throw)
            throw new InvalidOperationException(
                "IndexedDB is unavailable in this context. Set UnavailableAction = FallbackToMemory or ensure code runs in a browser.");
        _fallback = new MemoryKeyValueStore();
        return null;
    }
}
