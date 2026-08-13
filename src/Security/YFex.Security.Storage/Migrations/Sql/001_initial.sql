CREATE TABLE IF NOT EXISTS _migrations (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    name            TEXT    NOT NULL UNIQUE,
    applied_at      TEXT    NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS events (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp       TEXT    NOT NULL,
    process_id      INTEGER NOT NULL,
    process_name    TEXT    NOT NULL,
    event_type      TEXT    NOT NULL,
    detail_json     TEXT    NOT NULL,
    created_at      TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_events_timestamp ON events(timestamp);
CREATE INDEX IF NOT EXISTS idx_events_process ON events(process_name);
CREATE INDEX IF NOT EXISTS idx_events_type ON events(event_type);
CREATE INDEX IF NOT EXISTS idx_events_pid_time ON events(process_id, timestamp);

CREATE TABLE IF NOT EXISTS dns_queries (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp       TEXT    NOT NULL,
    process_id      INTEGER NOT NULL,
    process_name    TEXT    NOT NULL,
    query_name      TEXT    NOT NULL,
    query_type      TEXT    NOT NULL,
    resolved_ips    TEXT,
    created_at      TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_dns_domain ON dns_queries(query_name);
CREATE INDEX IF NOT EXISTS idx_dns_process ON dns_queries(process_name);

CREATE TABLE IF NOT EXISTS connections (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp       TEXT    NOT NULL,
    process_id      INTEGER NOT NULL,
    process_name    TEXT    NOT NULL,
    local_address   TEXT    NOT NULL,
    local_port      INTEGER NOT NULL,
    remote_address  TEXT    NOT NULL,
    remote_port     INTEGER NOT NULL,
    protocol        TEXT    NOT NULL,
    bytes_sent      INTEGER DEFAULT 0,
    bytes_received  INTEGER DEFAULT 0,
    duration_ms     REAL,
    created_at      TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_conn_remote ON connections(remote_address);
CREATE INDEX IF NOT EXISTS idx_conn_process ON connections(process_name);
CREATE INDEX IF NOT EXISTS idx_conn_time ON connections(timestamp);

CREATE TABLE IF NOT EXISTS http_captures (
    id                   INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp            TEXT    NOT NULL,
    process_id           INTEGER NOT NULL,
    process_name         TEXT    NOT NULL,
    method               TEXT    NOT NULL,
    url                  TEXT    NOT NULL,
    host                 TEXT    NOT NULL,
    path                 TEXT    NOT NULL,
    query_string         TEXT,
    request_headers      TEXT,
    request_body         TEXT,
    request_content_type TEXT,
    response_status      INTEGER,
    response_headers     TEXT,
    response_body        TEXT,
    response_content_type TEXT,
    response_size_bytes  INTEGER,
    duration_ms          REAL,
    capture_source       TEXT    NOT NULL,
    created_at           TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_http_host ON http_captures(host);
CREATE INDEX IF NOT EXISTS idx_http_url ON http_captures(url);
CREATE INDEX IF NOT EXISTS idx_http_process ON http_captures(process_name);

CREATE TABLE IF NOT EXISTS ws_frames (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp         TEXT    NOT NULL,
    process_id        INTEGER NOT NULL,
    process_name      TEXT    NOT NULL,
    connection_url    TEXT    NOT NULL,
    direction         TEXT    NOT NULL,
    opcode            INTEGER NOT NULL,
    text_payload      TEXT,
    binary_payload    BLOB,
    payload_length    INTEGER NOT NULL,
    created_at        TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_ws_url ON ws_frames(connection_url);

CREATE TABLE IF NOT EXISTS tls_handshakes (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp       TEXT    NOT NULL,
    process_id      INTEGER NOT NULL,
    process_name    TEXT    NOT NULL,
    sni             TEXT    NOT NULL,
    cipher_suite    TEXT,
    tls_version     TEXT,
    ja3_hash        TEXT,
    ja3s_hash       TEXT,
    cert_subject    TEXT,
    cert_issuer     TEXT,
    cert_expiry     TEXT,
    created_at      TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_tls_sni ON tls_handshakes(sni);
CREATE INDEX IF NOT EXISTS idx_tls_ja3 ON tls_handshakes(ja3_hash);
CREATE INDEX IF NOT EXISTS idx_tls_process ON tls_handshakes(process_name);

CREATE TABLE IF NOT EXISTS process_events (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp           TEXT    NOT NULL,
    process_id          INTEGER NOT NULL,
    process_name        TEXT    NOT NULL,
    event_type          TEXT    NOT NULL,
    parent_process_id   INTEGER,
    image_path          TEXT,
    command_line        TEXT,
    module_path         TEXT,
    is_signed           INTEGER,
    signer_name         TEXT,
    created_at          TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_proc_name ON process_events(process_name);
CREATE INDEX IF NOT EXISTS idx_proc_type ON process_events(event_type);

CREATE TABLE IF NOT EXISTS anomalies (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp       TEXT    NOT NULL,
    process_id      INTEGER NOT NULL,
    process_name    TEXT    NOT NULL,
    anomaly_type    TEXT    NOT NULL,
    description     TEXT    NOT NULL,
    severity        TEXT    NOT NULL,
    related_event   TEXT,
    acknowledged    INTEGER NOT NULL DEFAULT 0,
    created_at      TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_anomaly_type ON anomalies(anomaly_type);
CREATE INDEX IF NOT EXISTS idx_anomaly_severity ON anomalies(severity);

CREATE TABLE IF NOT EXISTS baselines (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    baseline_type   TEXT    NOT NULL,
    process_name    TEXT    NOT NULL,
    key             TEXT    NOT NULL,
    first_seen      TEXT    NOT NULL,
    last_seen       TEXT    NOT NULL,
    count           INTEGER NOT NULL DEFAULT 1,
    UNIQUE(baseline_type, process_name, key)
);

CREATE TABLE IF NOT EXISTS api_endpoints (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    service_name        TEXT    NOT NULL,
    endpoint_url        TEXT    NOT NULL,
    method              TEXT    NOT NULL,
    host                TEXT    NOT NULL,
    path_template       TEXT    NOT NULL,
    required_headers    TEXT,
    auth_type           TEXT,
    auth_details        TEXT,
    request_body_schema TEXT,
    response_body_schema TEXT,
    notes               TEXT,
    discovered_at       TEXT    NOT NULL,
    last_seen_at        TEXT    NOT NULL,
    call_count          INTEGER NOT NULL DEFAULT 1,
    UNIQUE(service_name, method, path_template)
);
CREATE INDEX IF NOT EXISTS idx_api_service ON api_endpoints(service_name);

CREATE TABLE IF NOT EXISTS ws_protocols (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    service_name        TEXT    NOT NULL,
    connection_url      TEXT    NOT NULL,
    subprotocol         TEXT,
    frame_type          TEXT    NOT NULL,
    direction           TEXT    NOT NULL,
    example_payload     TEXT,
    payload_schema      TEXT,
    notes               TEXT,
    discovered_at       TEXT    NOT NULL,
    UNIQUE(service_name, frame_type, direction)
);
