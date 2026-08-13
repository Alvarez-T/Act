using System.Net;
using System.Runtime.InteropServices;
using YFex.System.Windows.Security.Interop;

namespace YFex.System.Windows.Security.Network;

public sealed class NetworkInspector : INetworkInspector
{
    public IReadOnlyList<TcpConnectionInfo> GetTcpConnections()
    {
        var ipv4 = GetTcpTableV4();
        var ipv6 = GetTcpTableV6();

        var result = new List<TcpConnectionInfo>(ipv4.Count + ipv6.Count);
        result.AddRange(ipv4);
        result.AddRange(ipv6);
        return result;
    }

    public IReadOnlyList<UdpEndpointInfo> GetUdpEndpoints()
    {
        var ipv4 = GetUdpTableV4();
        var result = new List<UdpEndpointInfo>(ipv4);
        return result;
    }

    public IReadOnlyList<TcpConnectionInfo> GetTcpConnectionsByProcess(int pid)
        => GetTcpConnections().Where(c => c.OwningPid == pid).ToList();

    public IReadOnlyList<UdpEndpointInfo> GetUdpEndpointsByProcess(int pid)
        => GetUdpEndpoints().Where(e => e.OwningPid == pid).ToList();

    private static List<TcpConnectionInfo> GetTcpTableV4()
    {
        uint size = 0;
        IphlpapiInterop.GetExtendedTcpTable(
            nint.Zero, ref size, false,
            IphlpapiInterop.AF_INET, IphlpapiInterop.TCP_TABLE_OWNER_PID_ALL, 0);

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint result = IphlpapiInterop.GetExtendedTcpTable(
                buffer, ref size, false,
                IphlpapiInterop.AF_INET, IphlpapiInterop.TCP_TABLE_OWNER_PID_ALL, 0);

            if (result != IphlpapiInterop.NO_ERROR)
                return [];

            int count = Marshal.ReadInt32(buffer);
            var entries = new List<TcpConnectionInfo>(count);
            int offset = sizeof(int);
            int rowSize = Marshal.SizeOf<IphlpapiInterop.MIB_TCPROW_OWNER_PID>();

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<IphlpapiInterop.MIB_TCPROW_OWNER_PID>(buffer + offset);
                entries.Add(new TcpConnectionInfo(
                    new IPAddress(row.dwLocalAddr).ToString(),
                    SwapPort(row.dwLocalPort),
                    new IPAddress(row.dwRemoteAddr).ToString(),
                    SwapPort(row.dwRemotePort),
                    (TcpState)row.dwState,
                    (int)row.dwOwningPid));
                offset += rowSize;
            }

            return entries;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static List<TcpConnectionInfo> GetTcpTableV6()
    {
        uint size = 0;
        IphlpapiInterop.GetExtendedTcpTable(
            nint.Zero, ref size, false,
            IphlpapiInterop.AF_INET6, IphlpapiInterop.TCP_TABLE_OWNER_PID_ALL, 0);

        if (size == 0) return [];

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint result = IphlpapiInterop.GetExtendedTcpTable(
                buffer, ref size, false,
                IphlpapiInterop.AF_INET6, IphlpapiInterop.TCP_TABLE_OWNER_PID_ALL, 0);

            if (result != IphlpapiInterop.NO_ERROR)
                return [];

            int count = Marshal.ReadInt32(buffer);
            var entries = new List<TcpConnectionInfo>(count);
            int offset = sizeof(int);
            int rowSize = Marshal.SizeOf<IphlpapiInterop.MIB_TCP6ROW_OWNER_PID>();

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<IphlpapiInterop.MIB_TCP6ROW_OWNER_PID>(buffer + offset);
                entries.Add(new TcpConnectionInfo(
                    new IPAddress(row.ucLocalAddr).ToString(),
                    SwapPort(row.dwLocalPort),
                    new IPAddress(row.ucRemoteAddr).ToString(),
                    SwapPort(row.dwRemotePort),
                    (TcpState)row.dwState,
                    (int)row.dwOwningPid));
                offset += rowSize;
            }

            return entries;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static List<UdpEndpointInfo> GetUdpTableV4()
    {
        uint size = 0;
        IphlpapiInterop.GetExtendedUdpTable(
            nint.Zero, ref size, false,
            IphlpapiInterop.AF_INET, IphlpapiInterop.UDP_TABLE_OWNER_PID, 0);

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint result = IphlpapiInterop.GetExtendedUdpTable(
                buffer, ref size, false,
                IphlpapiInterop.AF_INET, IphlpapiInterop.UDP_TABLE_OWNER_PID, 0);

            if (result != IphlpapiInterop.NO_ERROR)
                return [];

            int count = Marshal.ReadInt32(buffer);
            var entries = new List<UdpEndpointInfo>(count);
            int offset = sizeof(int);
            int rowSize = Marshal.SizeOf<IphlpapiInterop.MIB_UDPROW_OWNER_PID>();

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<IphlpapiInterop.MIB_UDPROW_OWNER_PID>(buffer + offset);
                entries.Add(new UdpEndpointInfo(
                    new IPAddress(row.dwLocalAddr).ToString(),
                    SwapPort(row.dwLocalPort),
                    (int)row.dwOwningPid));
                offset += rowSize;
            }

            return entries;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static ushort SwapPort(uint networkOrderPort)
        => (ushort)IPAddress.NetworkToHostOrder((short)(networkOrderPort & 0xFFFF));
}
