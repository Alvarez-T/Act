namespace YFex.System.Data;

public record MonitorInfo(
    string Name,
    int WidthPx,
    int HeightPx,
    double Scaling,
    double Dpi,
    bool IsPrimary
);

public record DisplaySnapshot(
    MonitorInfo[] Monitors,
    string ThemeVariant,
    string? AccentColorHex,
    bool HighContrast
);
