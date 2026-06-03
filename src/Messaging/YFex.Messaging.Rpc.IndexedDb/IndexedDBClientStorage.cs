using Microsoft.JSInterop;
using YFex.Messaging.Rpc;

namespace YFex.Messaging.Rpc.IndexedDb;

/// <summary>
/// <see cref="IClientStorage"/> backed by the browser's IndexedDB API via JS interop.
/// Safe only when running inside a Blazor WASM context. Throws on server-side rendering
/// unless <see cref="IndexedDbStorageOptions.UnavailableAction"/> is set to
/// <see cref="IndexedDbUnavailableAction.FallbackToMemory"/>.
/// </summary>
public sealed class IndexedDBClientStorage : IClientStorage, IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly IndexedDbStorageOptions _opts;
    private IJSObjectReference? _module;
    private IClientStorage? _fallback;
    private bool _unavailable;

    public IndexedDBClientStorage(IJSRuntime js, IndexedDbStorageOptions opts)
    {
        _js = js;
        _opts = opts;
    }

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        var module = await GetModuleAsync(ct);
        if (module is null) return await _fallback!.GetAsync(key, ct);
        var result = await module.InvokeAsync<byte[]?>("yfexIdbGet", ct, key);
        return result;
    }

    public async ValueTask SetAsync(string key, byte[] value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var module = await GetModuleAsync(ct);
        if (module is null) { await _fallback!.SetAsync(key, value, ttl, ct); return; }
        var expiresAtMs = ttl.HasValue
            ? DateTimeOffset.UtcNow.Add(ttl.Value).ToUnixTimeMilliseconds()
            : 0L;
        await module.InvokeVoidAsync("yfexIdbSet", ct, key, value, expiresAtMs);
    }

    public async ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        var module = await GetModuleAsync(ct);
        if (module is null) { await _fallback!.DeleteAsync(key, ct); return; }
        await module.InvokeVoidAsync("yfexIdbDelete", ct, key);
    }

    public async ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default)
    {
        var module = await GetModuleAsync(ct);
        if (module is null) return await _fallback!.GetKeysWithPrefixAsync(prefix, ct);
        return await module.InvokeAsync<string[]>("yfexIdbGetKeysWithPrefix", ct, prefix);
    }

    public async ValueTask ClearAsync(CancellationToken ct = default)
    {
        var module = await GetModuleAsync(ct);
        if (module is null) { await _fallback!.ClearAsync(ct); return; }
        await module.InvokeVoidAsync("yfexIdbClear", ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
            await _module.DisposeAsync();
    }

    // ── module loading ───────────────────────────────────────────────────────

    private static readonly SemaphoreSlim _loadLock = new(1, 1);

    private async ValueTask<IJSObjectReference?> GetModuleAsync(CancellationToken ct)
    {
        if (_unavailable) return null;
        if (_module is not null) return _module;

        await _loadLock.WaitAsync(ct);
        try
        {
            if (_module is not null) return _module;
            _module = await _js.InvokeAsync<IJSObjectReference>(
                "import", ct, "./_content/YFex.Messaging.Rpc.IndexedDb/yfex-idb.js");
            return _module;
        }
        catch (Exception) when (!OperatingSystem.IsBrowser())
        {
            // SSR / non-browser context
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
        _fallback = new InMemoryClientStorage();
        return null;
    }
}
