// hook-websocket.js
// [PERSONAL USE] WebSocket frame capture at the TLS layer. Hooks the same
// SChannel / OpenSSL plaintext path and parses WebSocket frame headers (RFC 6455).
//
// Emits: { "type": "ws_frame", "pid": <n>,
//          "data": { url, direction, opcode, payload } }

'use strict';

const PID = Process.id;

function emit(data) {
    send(JSON.stringify({ type: 'ws_frame', pid: PID, data: data }));
}

// Parse a single RFC 6455 frame from a byte array. Returns {opcode, payload} or null.
function parseWsFrame(u8, direction) {
    if (u8.length < 2) return null;

    const b0 = u8[0];
    const b1 = u8[1];
    const opcode = b0 & 0x0f;
    const masked = (b1 & 0x80) !== 0;
    let len = b1 & 0x7f;
    let offset = 2;

    if (len === 126) {
        if (u8.length < 4) return null;
        len = (u8[2] << 8) | u8[3];
        offset = 4;
    } else if (len === 127) {
        if (u8.length < 10) return null;
        len = 0;
        for (let i = 0; i < 8; i++) len = (len * 256) + u8[2 + i];
        offset = 10;
    }

    let maskKey = null;
    if (masked) {
        if (u8.length < offset + 4) return null;
        maskKey = u8.slice(offset, offset + 4);
        offset += 4;
    }

    if (u8.length < offset + len) return null;

    let payload = '';
    for (let i = 0; i < len; i++) {
        let byte = u8[offset + i];
        if (masked && maskKey) byte ^= maskKey[i % 4];
        payload += String.fromCharCode(byte);
    }

    // Only surface text (1) and binary (2) frames as data.
    if (opcode !== 1 && opcode !== 2) return null;

    return { opcode: opcode, direction: direction, payload: payload, url: '' };
}

function attachWsLayer(name, addr, direction) {
    if (!addr) return;
    Interceptor.attach(addr, {
        onEnter(args) { this.buf = args[1]; this.num = args[2]; },
        onLeave(retval) {
            try {
                let len = this.num ? this.num.toInt32() : 0;
                if (name === 'SSL_read') len = retval.toInt32();
                if (len <= 0 || this.buf.isNull()) return;
                const bytes = this.buf.readByteArray(len);
                const frame = parseWsFrame(new Uint8Array(bytes), direction);
                if (frame) emit(frame);
            } catch (e) {}
        }
    });
}

function findSsl(name) {
    for (const m of Process.enumerateModules()) {
        const lower = m.name.toLowerCase();
        if (lower.indexOf('ssl') >= 0 || lower.indexOf('boringssl') >= 0) {
            const exp = Module.findExportByName(m.name, name);
            if (exp) return exp;
        }
    }
    return Module.findExportByName(null, name);
}

attachWsLayer('SSL_write', findSsl('SSL_write'), 'sent');
attachWsLayer('SSL_read', findSsl('SSL_read'), 'received');
