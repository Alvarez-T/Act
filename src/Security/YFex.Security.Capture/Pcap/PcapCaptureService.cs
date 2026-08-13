using Microsoft.Extensions.Hosting;
using PacketDotNet;
using SharpPcap;
using YFex.Security.Capture.Correlation;
using YFex.Security.Events;
using YFex.Security.Pipeline;

namespace YFex.Security.Capture.Pcap;

public sealed class PcapCaptureService : BackgroundService
{
    private readonly ISecurityPipeline _pipeline;
    private readonly ProcessSocketCorrelator _correlator;
    private readonly string? _interfaceName;
    private readonly string _bpfFilter;
    private ILiveDevice? _device;

    public PcapCaptureService(
        ISecurityPipeline pipeline,
        ProcessSocketCorrelator correlator,
        string? interfaceName = null,
        string bpfFilter = "tcp port 443")
    {
        _pipeline = pipeline;
        _correlator = correlator;
        _interfaceName = interfaceName;
        _bpfFilter = bpfFilter;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.Run(() => RunCapture(stoppingToken), stoppingToken);
    }

    private void RunCapture(CancellationToken ct)
    {
        var devices = CaptureDeviceList.Instance;
        if (devices.Count == 0) return;

        _device = _interfaceName is not null
            ? devices.FirstOrDefault(d => d.Name.Contains(_interfaceName, StringComparison.OrdinalIgnoreCase))
              ?? devices[0]
            : devices[0];

        _device.OnPacketArrival += OnPacketArrival;
        _device.Open(DeviceModes.Promiscuous, 1000);

        if (!string.IsNullOrEmpty(_bpfFilter))
            _device.Filter = _bpfFilter;

        ct.Register(() =>
        {
            try
            {
                _device.StopCapture();
                _device.Close();
            }
            catch { }
        });

        _device.StartCapture();

        ct.WaitHandle.WaitOne();
    }

    private void OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var rawPacket = e.GetPacket();
            var packet = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);

            var ipPacket = packet.Extract<IPPacket>();
            var tcpPacket = packet.Extract<TcpPacket>();

            if (ipPacket is null || tcpPacket is null) return;

            if (tcpPacket.PayloadData is null || tcpPacket.PayloadData.Length < 6)
                return;

            // Only process TLS ClientHello (content type 0x16, handshake type 0x01)
            if (tcpPacket.PayloadData[0] != 0x16) return;
            if (tcpPacket.PayloadData.Length < 6 || tcpPacket.PayloadData[5] != 0x01) return;

            var (ja3Hash, ja3Raw, sni) = Ja3Calculator.ComputeFromClientHello(tcpPacket.PayloadData);

            if (string.IsNullOrEmpty(ja3Hash)) return;

            string localAddr = ipPacket.SourceAddress.ToString();
            int localPort = tcpPacket.SourcePort;
            string remoteAddr = ipPacket.DestinationAddress.ToString();
            int remotePort = tcpPacket.DestinationPort;

            int pid = _correlator.GetOwnerPid(localAddr, localPort, remoteAddr, remotePort) ?? 0;
            string processName = pid > 0 ? (_correlator.GetProcessName(pid) ?? "") : "";

            var evt = new TlsHandshakeEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                ProcessId = pid,
                ProcessName = processName,
                Sni = sni,
                Ja3Hash = ja3Hash,
                TlsVersion = ExtractTlsVersion(tcpPacket.PayloadData)
            };

            _pipeline.Writer.TryWrite(evt);
        }
        catch { }
    }

    private static string ExtractTlsVersion(byte[] data)
    {
        if (data.Length < 3) return "";
        ushort version = (ushort)((data[1] << 8) | data[2]);
        return version switch
        {
            0x0301 => "TLS 1.0",
            0x0302 => "TLS 1.1",
            0x0303 => "TLS 1.2",
            0x0304 => "TLS 1.3",
            _ => $"0x{version:X4}"
        };
    }

    public override void Dispose()
    {
        _device?.Dispose();
        base.Dispose();
    }
}
