namespace YFex.System.Windows.Security.Registry;

public record AutostartEntry(
    string Location,
    string Type,
    string Name,
    string Value,
    int RiskScore);
