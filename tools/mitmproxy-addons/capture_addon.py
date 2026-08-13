# capture_addon.py
# [PERSONAL USE] mitmproxy addon that serializes each HTTP flow and WebSocket
# message to a single JSON line on stdout, consumed by YFex.Security.Proxy.
#
# Run:  mitmdump -p 8080 -s capture_addon.py -q
#
# Output lines:
#   {"type":"http","method":"GET","url":"https://...","host":"...","path":"/...",
#    "query_string":"...","request_headers":{...},"request_body":"...",
#    "response_status":200,"response_headers":{...},"response_body":"...",
#    "duration_ms":12.3,"client_port":54321}
#
#   {"type":"websocket","url":"wss://...","direction":"sent","opcode":1,
#    "payload":"...","client_port":54321}

import json
import sys

from mitmproxy import http


MAX_BODY = 256 * 1024  # cap body size to avoid flooding stdout


def _emit(obj):
    try:
        sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
        sys.stdout.flush()
    except Exception:
        pass


def _decode_body(message):
    try:
        raw = message.get_text(strict=False)
        if raw is None:
            return None
        if len(raw) > MAX_BODY:
            return raw[:MAX_BODY]
        return raw
    except Exception:
        return None


def _client_port(flow):
    try:
        return flow.client_conn.peername[1]
    except Exception:
        return None


class CaptureAddon:
    def response(self, flow: http.HTTPFlow):
        req = flow.request
        res = flow.response

        duration = None
        try:
            if flow.response and flow.request and flow.response.timestamp_end and flow.request.timestamp_start:
                duration = (flow.response.timestamp_end - flow.request.timestamp_start) * 1000.0
        except Exception:
            duration = None

        _emit({
            "type": "http",
            "method": req.method,
            "url": req.pretty_url,
            "host": req.pretty_host,
            "path": req.path.split("?")[0],
            "query_string": req.path.split("?")[1] if "?" in req.path else "",
            "request_headers": dict(req.headers),
            "request_body": _decode_body(req),
            "request_content_type": req.headers.get("content-type"),
            "response_status": res.status_code if res else None,
            "response_headers": dict(res.headers) if res else None,
            "response_body": _decode_body(res) if res else None,
            "response_content_type": res.headers.get("content-type") if res else None,
            "duration_ms": duration,
            "client_port": _client_port(flow),
        })

    def websocket_message(self, flow: http.HTTPFlow):
        if not flow.websocket or not flow.websocket.messages:
            return
        msg = flow.websocket.messages[-1]

        try:
            payload = msg.content.decode("utf-8", errors="replace")
        except Exception:
            payload = ""

        _emit({
            "type": "websocket",
            "url": flow.request.pretty_url if flow.request else "",
            "direction": "sent" if msg.from_client else "received",
            "opcode": 1 if msg.is_text else 2,
            "payload": payload[:MAX_BODY],
            "client_port": _client_port(flow),
        })


addons = [CaptureAddon()]
