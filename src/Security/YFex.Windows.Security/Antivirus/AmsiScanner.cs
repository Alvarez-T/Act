using System.Runtime.InteropServices;
using YFex.Windows.Security.Interop;

namespace YFex.Windows.Security.Antivirus;

public sealed class AmsiScanner : IAmsiScanner, IDisposable
{
    private nint _context;

    public bool IsAvailable { get; }

    public AmsiScanner()
    {
        try
        {
            int hr = AmsiInterop.AmsiInitialize("YFex.Security", out _context);
            IsAvailable = hr == 0 && _context != nint.Zero;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    public AmsiScanResult ScanBuffer(byte[] buffer, string contentName = "")
    {
        if (!IsAvailable) return new NotDetected();

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            int hr = AmsiInterop.AmsiScanBuffer(
                _context, handle.AddrOfPinnedObject(), (uint)buffer.Length,
                contentName, nint.Zero, out int result);

            if (hr != 0) return new NotDetected();
            return InterpretResult(result);
        }
        finally
        {
            handle.Free();
        }
    }

    public AmsiScanResult ScanFile(string filePath)
    {
        if (!File.Exists(filePath)) return new NotDetected();
        var buffer = File.ReadAllBytes(filePath);
        return ScanBuffer(buffer, filePath);
    }

    private static AmsiScanResult InterpretResult(int result) => result switch
    {
        AmsiInterop.AMSI_RESULT_CLEAN => new Clean(),
        AmsiInterop.AMSI_RESULT_NOT_DETECTED => new NotDetected(),
        >= AmsiInterop.AMSI_RESULT_BLOCKED_BY_ADMIN_START
            and <= AmsiInterop.AMSI_RESULT_BLOCKED_BY_ADMIN_END => new BlockedByAdmin(),
        >= AmsiInterop.AMSI_RESULT_DETECTED => new Detected("Malware detected by AMSI provider"),
        _ => new NotDetected()
    };

    public void Dispose()
    {
        if (_context != nint.Zero)
        {
            AmsiInterop.AmsiUninitialize(_context);
            _context = nint.Zero;
        }
    }
}
