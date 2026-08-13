namespace YFex.Security.Network;

public enum TcpState
{
    Closed = 1,
    Listen = 2,
    SynSent = 3,
    SynReceived = 4,
    Established = 5,
    FinWait1 = 6,
    FinWait2 = 7,
    CloseWait = 8,
    Closing = 9,
    LastAck = 10,
    TimeWait = 11,
    DeleteTcb = 12
}

public record TcpConnectionInfo(
    string LocalAddress,
    ushort LocalPort,
    string RemoteAddress,
    ushort RemotePort,
    TcpState State,
    int OwningPid
);

public record UdpEndpointInfo(
    string LocalAddress,
    ushort LocalPort,
    int OwningPid
);
