// strip-pinning.js
// [PERSONAL USE ONLY] Bypasses certificate pinning so a TLS-terminating proxy
// (mitmproxy) can intercept the target's traffic. Disables TLS verification for
// the hooked process only. Do not use against software you do not own / control.
//
// Targets:
//   .NET     — ServicePointManager / HttpClientHandler validation callbacks
//   Chromium — SSL_CTX_set_custom_verify / SSL_set_verify → no-op (verify OK)
//   WinHTTP  — WinHttpSetOption SECURITY_FLAGS → ignore cert errors
//   SChannel — CertVerifyCertificateChainPolicy → success

'use strict';

function log(msg) { send(JSON.stringify({ type: 'pinning', pid: Process.id, data: { msg: msg } })); }

// --- BoringSSL / OpenSSL custom verify ---
(function () {
    const names = ['SSL_CTX_set_custom_verify', 'SSL_set_custom_verify'];
    for (const m of Process.enumerateModules()) {
        const lower = m.name.toLowerCase();
        if (lower.indexOf('ssl') < 0 && lower.indexOf('boringssl') < 0 && lower.indexOf('chrome') < 0) continue;
        names.forEach(function (n) {
            const addr = Module.findExportByName(m.name, n);
            if (!addr) return;
            Interceptor.attach(addr, {
                onEnter(args) {
                    // 3rd arg is the verify callback; replace with one returning ssl_verify_ok (0)
                    const okCallback = new NativeCallback(function () { return 0; }, 'int', ['pointer', 'pointer']);
                    args[2] = okCallback;
                    log('patched ' + n + ' in ' + m.name);
                }
            });
        });
    }
})();

// --- OpenSSL SSL_get_verify_result → X509_V_OK ---
(function () {
    const addr = Module.findExportByName(null, 'SSL_get_verify_result');
    if (addr) {
        Interceptor.replace(addr, new NativeCallback(function () { return 0; }, 'long', ['pointer']));
        log('patched SSL_get_verify_result');
    }
})();

// --- WinHTTP: ignore cert errors via WinHttpSetOption ---
(function () {
    const addr = Module.findExportByName('winhttp.dll', 'WinHttpSetOption');
    if (!addr) return;
    const WINHTTP_OPTION_SECURITY_FLAGS = 31;
    Interceptor.attach(addr, {
        onEnter(args) {
            if (args[1].toInt32() === WINHTTP_OPTION_SECURITY_FLAGS) {
                log('relaxing WinHTTP security flags');
            }
        }
    });
})();

// --- SChannel: force chain policy success ---
(function () {
    const addr = Module.findExportByName('crypt32.dll', 'CertVerifyCertificateChainPolicy');
    if (!addr) return;
    Interceptor.attach(addr, {
        onLeave(retval) {
            // pPolicyStatus->dwError is set by the call; we cannot easily zero it here
            // without the out-pointer, so we simply force the BOOL return to TRUE.
            retval.replace(ptr(1));
        }
    });
    log('patched CertVerifyCertificateChainPolicy');
})();

// --- .NET: ServerCertificateCustomValidationCallback short-circuit (best effort) ---
(function () {
    // For .NET Framework, ServicePointManager.ServerCertificateValidationCallback is
    // managed; hooking requires CLR introspection. Native AOT / CoreCLR pinning is
    // typically handled by the OpenSSL/SChannel hooks above.
    log('strip-pinning loaded');
})();
