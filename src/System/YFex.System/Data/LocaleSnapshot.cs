namespace YFex.System.Data;

public record LocaleSnapshot(
    string Culture,
    string UICulture,
    string TimeZoneId,
    int UtcOffsetMinutes,
    string InputLanguage
);
