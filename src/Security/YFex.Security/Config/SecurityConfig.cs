namespace YFex.Security.Config;

public sealed class SecurityConfig
{
    public string DatabasePath { get; set; } = @"%LocalAppData%\YFex\security.db";
    public string LogDirectory { get; set; } = @"%LocalAppData%\YFex\logs";
    public string FridaPath { get; set; } = "frida";
    public string MitmdumpPath { get; set; } = "mitmdump";
    public int ProxyPort { get; set; } = 8080;
    public int MaxEventsPerDay { get; set; } = 1_000_000;
    public int RetentionDays { get; set; } = 90;
    public bool EnableDnsCapture { get; set; } = true;
    public bool EnableProcessCapture { get; set; } = true;
    public bool EnableFileIoCapture { get; set; }
    public bool EnableTlsCapture { get; set; } = true;
    public int BaselineWindowDays { get; set; } = 14;
    public HashSet<string> KnownNetworkProcesses { get; set; } = [];
    public HashSet<string> AllowedDomains { get; set; } = [];
}
