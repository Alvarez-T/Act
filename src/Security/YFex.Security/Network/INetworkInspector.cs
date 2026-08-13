namespace YFex.Security.Network;

public interface INetworkInspector
{
    IReadOnlyList<TcpConnectionInfo> GetTcpConnections();
    IReadOnlyList<UdpEndpointInfo> GetUdpEndpoints();
    IReadOnlyList<TcpConnectionInfo> GetTcpConnectionsByProcess(int pid);
    IReadOnlyList<UdpEndpointInfo> GetUdpEndpointsByProcess(int pid);
}
