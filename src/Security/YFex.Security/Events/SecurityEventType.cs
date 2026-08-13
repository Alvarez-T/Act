namespace YFex.Security.Events;

public enum SecurityEventType
{
    TcpConnect,
    TcpDisconnect,
    UdpSend,
    UdpReceive,
    DnsQuery,
    DnsResponse,
    TlsHandshake,
    TlsCertChain,
    HttpRequest,
    HttpResponse,
    WebSocketFrame,
    GrpcCall,
    GraphQlOperation,
    SseEvent,
    RawPreTlsBuffer,
    ProcessStart,
    ProcessStop,
    ModuleLoad,
    RemoteThreadCreate,
    FileRead,
    FileWrite,
    AnomalyDetected,
    BaselineDeviation
}
