namespace YFex.Windows.Security.Antivirus;

public record Clean;
public record NotDetected;
public record Detected(string ThreatName);
public record BlockedByAdmin;

public union AmsiScanResult(Clean, NotDetected, Detected, BlockedByAdmin);
