// hook-openssl.js
// [PERSONAL USE] Hooks OpenSSL / BoringSSL / LibreSSL SSL_write / SSL_read to read
// plaintext before encryption and after decryption. Works for Electron/Chromium,
// Python, curl, Node.
//
// Hooks: SSL_write(ssl, buf, num), SSL_read(ssl, buf, num)
//
// Emits: { "type": "http_request"|"raw_write", "pid": <n>, "data": {...} }

'use strict';

const PID = Process.id;

function emit(type, data) {
    send(JSON.stringify({ type: type, pid: PID, data: data }));
}

function looksLikeHttp(s) {
    return /^(GET |POST |PUT |DELETE |PATCH |HEAD |OPTIONS |HTTP\/1\.)/.test(s.substring(0, 16));
}

function parseHttp(text) {
    const lines = text.split('\r\n');
    const requestLine = lines[0].split(' ');
    const headers = {};
    let i = 1;
    for (; i < lines.length; i++) {
        if (lines[i] === '') { i++; break; }
        const idx = lines[i].indexOf(':');
        if (idx > 0) headers[lines[i].substring(0, idx).trim()] = lines[i].substring(idx + 1).trim();
    }
    const body = lines.slice(i).join('\r\n');
    return {
        method: requestLine[0] || '',
        path: requestLine[1] || '',
        host: headers['Host'] || '',
        url: headers['Host'] ? 'https://' + headers['Host'] + (requestLine[1] || '') : (requestLine[1] || ''),
        headers: headers,
        body: body
    };
}

function readBuffer(buf, num) {
    if (buf.isNull() || num <= 0) return null;
    const bytes = buf.readByteArray(num);
    const u8 = new Uint8Array(bytes);
    let s = '';
    for (let i = 0; i < u8.length; i++) s += String.fromCharCode(u8[i]);
    return s;
}

function handle(s) {
    if (s === null) return;
    if (looksLikeHttp(s)) {
        emit('http_request', parseHttp(s));
    } else {
        emit('raw_write', { base64: btoa(s) });
    }
}

function findSslFunction(name) {
    const modules = Process.enumerateModules();
    for (const m of modules) {
        const lower = m.name.toLowerCase();
        if (lower.indexOf('ssl') >= 0 || lower.indexOf('crypto') >= 0 || lower.indexOf('boringssl') >= 0) {
            const exp = Module.findExportByName(m.name, name);
            if (exp) return exp;
        }
    }
    return Module.findExportByName(null, name);
}

const sslWrite = findSslFunction('SSL_write');
if (sslWrite) {
    Interceptor.attach(sslWrite, {
        onEnter(args) {
            try { handle(readBuffer(args[1], args[2].toInt32())); } catch (e) {}
        }
    });
}

const sslRead = findSslFunction('SSL_read');
if (sslRead) {
    Interceptor.attach(sslRead, {
        onEnter(args) { this.buf = args[1]; },
        onLeave(retval) {
            const n = retval.toInt32();
            if (n > 0) { try { handle(readBuffer(this.buf, n)); } catch (e) {} }
        }
    });
}
