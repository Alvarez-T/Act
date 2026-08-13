// YFex IndexedDB interop layer
// Exported functions are called from IndexedDBClientStorage via IJSRuntime.
// All operations target the "yfex-storage" database, object store "kv".

const DB_NAME = 'yfex-storage';
const DB_VERSION = 1;
const STORE = 'kv';

let _db = null;

function openDb() {
    if (_db) return Promise.resolve(_db);
    return new Promise((resolve, reject) => {
        const req = indexedDB.open(DB_NAME, DB_VERSION);
        req.onupgradeneeded = e => {
            const db = e.target.result;
            if (!db.objectStoreNames.contains(STORE)) {
                db.createObjectStore(STORE, { keyPath: 'key' });
            }
        };
        req.onsuccess = e => { _db = e.target.result; resolve(_db); };
        req.onerror = e => reject(e.target.error);
    });
}

function tx(mode) {
    return _db.transaction(STORE, mode).objectStore(STORE);
}

function request(req) {
    return new Promise((resolve, reject) => {
        req.onsuccess = e => resolve(e.target.result);
        req.onerror = e => reject(e.target.error);
    });
}

export async function yfexIdbGet(key) {
    await openDb();
    const record = await request(tx('readonly').get(key));
    if (!record) return null;
    if (record.expiresAt && record.expiresAt < Date.now()) {
        // lazy expiry
        await yfexIdbDelete(key);
        return null;
    }
    return record.value; // Uint8Array
}

export async function yfexIdbSet(key, value, expiresAtMs) {
    await openDb();
    const record = { key, value };
    if (expiresAtMs > 0) record.expiresAt = expiresAtMs;
    await request(tx('readwrite').put(record));
}

export async function yfexIdbDelete(key) {
    await openDb();
    await request(tx('readwrite').delete(key));
}

export async function yfexIdbGetKeysWithPrefix(prefix) {
    await openDb();
    const store = tx('readonly');
    const range = IDBKeyRange.bound(prefix, prefix + '￿', false, false);
    const keys = await request(store.getAllKeys(range));
    return keys;
}

export async function yfexIdbClear() {
    await openDb();
    await request(tx('readwrite').clear());
}
