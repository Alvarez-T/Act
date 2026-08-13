namespace YFex.System.Telemetry;

public class TelemetryOptions
{
    public Uri Endpoint { get; set; } = null!;
    public string AppId { get; set; } = "";
    public int BatchSize { get; set; } = 50;
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxQueueSize { get; set; } = 5000;
    public bool DropOnFull { get; set; } = true;
    public string TransportSaltKey { get; set; } = "yfex.telemetry";
}
