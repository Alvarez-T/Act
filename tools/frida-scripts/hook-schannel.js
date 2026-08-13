// hook-schannel.js
// [PERSONAL USE] Hooks Windows SChannel (sspicli.dll) to read plaintext before
// TLS encryption and after decryption. Works for .NET apps, Edge, native Windows apps.
//
// Hooks:
//   sspicli.dll!EncryptMessage  — buffer[1] holds plaintext before encryption
//   sspicli.dll!DecryptMessage  — buffer[1] holds plaintext after decryption
//
// Emits one JSON line per captured buffer:
//   { "type": "http_request", "pid": <n>, "data": { method, url, host, path, headers, body } }
//   { "type": "raw_write",    "pid": <n>, "data": { base64 } }

'use strict';

const PID = Process.id;

function emit(type, data) {
    send(JSON.stringify({ type: type, pid: PID, data: data }));
}

// SecBufferDesc: { ULONG ulVersion; ULONG cBuffers; PSecBuffer pBuffers; }
// SecBuffer:     { ULONG cbBuffer; ULONG BufferType; PVOID pvBuffer; }
const PTR = Process.pointerSize;
const SECBUFFER_DATA = 1;

function readSecBuffers(pMessage) {
    if (pMessage.isNull()) return [];
    const cBuffers = pMessage.add(PTR === 8 ? 4 : 4).readU32();
    const pBuffers = pMessage.add(PTR === 8 ? 8 : 8).readPointer();
    const out = [];
    for (let i = 0; i < cBuffers; i++) {
        // sizeof(SecBuffer) = 8 + PTR (cbBuffer:4, BufferType:4, pvBuffer:PTR), aligned
        const stride = (PTR === 8) ? 16 : 12;
        const entry = pBuffers.add(i * stride);
        const cb = entry.readU32();
        const bufferType = entry.add(4).readU32();
        const pv = entry.add(8).readPointer();
        if (bufferType === SECBUFFER_DATA && cb > 0 && !pv.isNull()) {
            out.push(pv.readByteArray(cb));
        }
    }
    return out;
}

function looksLikeHttp(bytes) {
    const head = String.fromCharCode.apply(null, new Uint8Array(bytes.slice(0, 16)));
    return /^(GET |POST |PUT |DELETE |PATCH |HEAD |OPTIONS |HTTP\/1\.)/.test(head);
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
        url: (headers['Host'] ? 'https://' + headers['Host'] + (requestLine[1] || '') : (requestLine[1] || '')),
        headers: headers,
        body: body
    };
}

function handleBuffer(bytes) {
    if (looksLikeHttp(bytes)) {
        const text = Memory.allocUtf8String ? '' : '';
        const u8 = new Uint8Array(bytes);
        let s = '';
        for (let i = 0; i < u8.length; i++) s += String.fromCharCode(u8[i]);
        emit('http_request', parseHttp(s));
    } else {
        const u8 = new Uint8Array(bytes);
        let bin = '';
        for (let i = 0; i < u8.length; i++) bin += String.fromCharCode(u8[i]);
        emit('raw_write', { base64: btoa(bin) });
    }
}

['EncryptMessage', 'DecryptMessage'].forEach(function (fn) {
    const addr = Module.findExportByName('sspicli.dll', fn)
        || Module.findExportByName('secur32.dll', fn);
    if (!addr) return;

    Interceptor.attach(addr, {
        onEnter(args) {
            this.pMessage = (fn === 'EncryptMessage') ? args[2] : args[1];
            if (fn === 'EncryptMessage') {
                try { readSecBuffers(this.pMessage).forEach(handleBuffer); } catch (e) {}
            }
        },
        onLeave(retval) {
            if (fn === 'DecryptMessage' && retval.toInt32() === 0) {
                try { readSecBuffers(this.pMessage).forEach(handleBuffer); } catch (e) {}
            }
        }
    });
});
