namespace YFex.System.Data;

public record InstalledApp(
    string Name,
    string Version,
    string Publisher,
    string InstallDate
);

public record InstalledAppList(IReadOnlyList<InstalledApp> Apps);
