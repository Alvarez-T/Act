using YFex.System.Consent;
using YFex.System.Data;
using YFex.System.Platform;
using YFex.System.Unions;

namespace YFex.System.Telemetry;

public sealed class TelemetryCollector : ITelemetryCollector
{
    private readonly IPlatformProvider _platform;
    private readonly ITelemetryConsent _consent;
    private readonly TelemetryBatch _batch;
    private readonly IUserBehaviourTracker _behaviour;
    private readonly IOfflineQueue _offlineQueue;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private string? _deviceIdHash;

    public TelemetryCollector(
        IPlatformProvider platform,
        ITelemetryConsent consent,
        TelemetryBatch batch,
        IUserBehaviourTracker behaviour,
        IOfflineQueue offlineQueue)
    {
        _platform = platform;
        _consent = consent;
        _batch = batch;
        _behaviour = behaviour;
        _offlineQueue = offlineQueue;

        consent.ConsentDowngraded += OnConsentDowngraded;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_platform.Fingerprint.GetDeviceId() is Available<string> fp)
            _deviceIdHash = fp.Value;

        await _batch.StartAsync(ct);
    }

    public void Track(string eventName, Dictionary<string, object?>? properties = null)
    {
        try
        {
            var evt = new TelemetryEvent(
                eventName,
                _sessionId,
                _deviceIdHash ?? "unknown",
                DateTimeOffset.UtcNow,
                _consent.CurrentState,
                properties ?? []);
            _batch.Enqueue(evt);
        }
        catch { /* telemetry must never crash the host */ }
    }

    public Task<SnapshotResult> CollectSnapshotAsync(CancellationToken ct)
    {
        try
        {
            var fields = new List<DataField>();
            var errors = new List<CollectionError>();
            bool analytics = _consent.IsAllowed(ConsentLevel.Analytics);
            bool full = _consent.IsAllowed(ConsentLevel.Full);

            if (_consent.IsAllowed(ConsentLevel.Functional))
                CollectSystemData(fields, errors, analytics);

            if (analytics)
            {
                CollectLocaleData(fields, errors);
                CollectNetworkData(fields, errors);
                CollectDisplayData(fields, errors);
                CollectIdleData(fields, errors);
                CollectBehaviourData(fields, errors);
            }

            if (full)
            {
                CollectInstalledSoftwareData(fields, errors);
                CollectActiveWindowData(fields, errors);
                CollectBrowserData(fields, errors);
            }

            SnapshotResult result = new PartialResult([.. fields], [.. errors]);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            SnapshotResult result = new CollectionError("snapshot", ex);
            return Task.FromResult(result);
        }
    }

    public Task FlushAsync(CancellationToken ct) => _batch.FlushAsync(ct);

    public async ValueTask DisposeAsync()
    {
        _consent.ConsentDowngraded -= OnConsentDowngraded;
        await _batch.DisposeAsync();
    }

    private async void OnConsentDowngraded(ConsentLevel oldLevel, ConsentLevel newLevel)
    {
        try { await FlushAsync(CancellationToken.None); }
        catch { }
    }

    private void CollectSystemData(List<DataField> fields, List<CollectionError> errors, bool analyticsLevel)
    {
        try
        {
            var sys = _platform.CaptureSystemSnapshot();
            fields.Add(new DataField("os", sys.OsDescription, "system"));
            fields.Add(new DataField("osArch", sys.OsArchitecture, "system"));
            fields.Add(new DataField("is64Bit", sys.Is64BitOs, "system"));
            fields.Add(new DataField("runtime", sys.RuntimeVersion, "system"));
            fields.Add(new DataField("processors", sys.ProcessorCount, "system"));
            fields.Add(new DataField("appVersion", sys.AppVersion, "system"));

            if (analyticsLevel)
            {
                fields.Add(new DataField("workingSetMb", sys.WorkingSetBytes / (1024 * 1024), "system"));
                fields.Add(new DataField("gcMemoryMb", sys.GcTotalMemory / (1024 * 1024), "system"));
                fields.Add(new DataField("machineName", sys.MachineName, "system"));
                fields.Add(new DataField("userName", sys.UserName, "system"));
                fields.Add(new DataField("isElevated", sys.IsElevated, "system"));
                fields.Add(new DataField("appUptimeMin", sys.AppUptimeMs / 60000, "system"));
            }
        }
        catch (Exception ex) { errors.Add(new CollectionError("system", ex)); }
    }

    private void CollectLocaleData(List<DataField> fields, List<CollectionError> errors)
    {
        try
        {
            var locale = _platform.CaptureLocaleSnapshot();
            fields.Add(new DataField("culture", locale.Culture, "locale"));
            fields.Add(new DataField("uiCulture", locale.UICulture, "locale"));
            fields.Add(new DataField("timezone", locale.TimeZoneId, "locale"));
            fields.Add(new DataField("utcOffset", locale.UtcOffsetMinutes, "locale"));
            fields.Add(new DataField("inputLanguage", locale.InputLanguage, "locale"));
        }
        catch (Exception ex) { errors.Add(new CollectionError("locale", ex)); }
    }

    private void CollectNetworkData(List<DataField> fields, List<CollectionError> errors)
    {
        try
        {
            var net = _platform.CaptureNetworkSnapshot();
            fields.Add(new DataField("networkAccess", net.HasNetworkAccess, "network"));
            fields.Add(new DataField("adapterCount", net.Adapters.Length, "network"));
            foreach (var adapter in net.Adapters.Where(a => a.IsUp))
            {
                fields.Add(new DataField($"adapter_{adapter.Name}_mac", adapter.HashedMac, "network"));
                fields.Add(new DataField($"adapter_{adapter.Name}_speed", adapter.SpeedBps, "network"));
            }
        }
        catch (Exception ex) { errors.Add(new CollectionError("network", ex)); }
    }

    private void CollectDisplayData(List<DataField> fields, List<CollectionError> errors)
    {
        try
        {
            if (_platform.Display.Capture() is Available<DisplaySnapshot> display)
            {
                var snap = display.Value;
                fields.Add(new DataField("monitorCount", snap.Monitors.Length, "display"));
                fields.Add(new DataField("theme", snap.ThemeVariant, "display"));
                fields.Add(new DataField("highContrast", snap.HighContrast, "display"));
                if (snap.AccentColorHex is not null)
                    fields.Add(new DataField("accentColor", snap.AccentColorHex, "display"));

                for (int i = 0; i < snap.Monitors.Length; i++)
                {
                    var m = snap.Monitors[i];
                    fields.Add(new DataField($"monitor{i}_res", $"{m.WidthPx}x{m.HeightPx}", "display"));
                    fields.Add(new DataField($"monitor{i}_dpi", m.Dpi, "display"));
                    fields.Add(new DataField($"monitor{i}_scale", m.Scaling, "display"));
                    fields.Add(new DataField($"monitor{i}_primary", m.IsPrimary, "display"));
                }
            }
        }
        catch (Exception ex) { errors.Add(new CollectionError("display", ex)); }
    }

    private void CollectIdleData(List<DataField> fields, List<CollectionError> errors)
    {
        try
        {
            if (_platform.IdleDetector.GetCurrentIdleState() is Available<IdleState> idle)
            {
                fields.Add(new DataField("idleSeconds", idle.Value.IdleDuration.TotalSeconds, "session"));
                fields.Add(new DataField("isIdle", idle.Value.IsIdle, "session"));
            }
        }
        catch (Exception ex) { errors.Add(new CollectionError("idle", ex)); }
    }

    private void CollectBehaviourData(List<DataField> fields, List<CollectionError> errors)
    {
        try
        {
            var b = _behaviour.GetSnapshot();
            fields.Add(new DataField("clicks", b.ClickCount, "behaviour"));
            fields.Add(new DataField("keystrokes", b.KeystrokeCount, "behaviour"));
            fields.Add(new DataField("scrolls", b.ScrollCount, "behaviour"));
            fields.Add(new DataField("resizes", b.WindowResizeCount, "behaviour"));
            fields.Add(new DataField("activeMinutes", b.ActiveDuration.TotalMinutes, "behaviour"));
            fields.Add(new DataField("idleMinutes", b.IdleDuration.TotalMinutes, "behaviour"));
        }
        catch (Exception ex) { errors.Add(new CollectionError("behaviour", ex)); }
    }

    private void CollectInstalledSoftwareData(List<DataField> fields, List<CollectionError> errors)
    {
        try
        {
            if (_platform.InstalledSoftware.GetInstalledSoftware() is Available<InstalledAppList> apps)
            {
                fields.Add(new DataField("installedAppCount", apps.Value.Apps.Count, "software"));
                var publishers = apps.Value.Apps
                    .Where(a => !string.IsNullOrEmpty(a.Publisher))
                    .GroupBy(a => a.Publisher)
                    .OrderByDescending(g => g.Count())
                    .Take(20)
                    .Select(g => g.Key)
                    .ToArray();
                fields.Add(new DataField("topPublishers", string.Join(", ", publishers), "software"));
            }
        }
        catch (Exception ex) { errors.Add(new CollectionError("software", ex)); }
    }

    private void CollectActiveWindowData(List<DataField> fields, List<CollectionError> errors)
    {
        try
        {
            if (_platform.ActiveWindow.GetActiveWindow() is Available<ActiveWindowInfo> win)
            {
                fields.Add(new DataField("activeProcess", win.Value.ProcessName, "window"));
                fields.Add(new DataField("activeProcessId", win.Value.ProcessId, "window"));
            }
        }
        catch (Exception ex) { errors.Add(new CollectionError("window", ex)); }
    }

    private void CollectBrowserData(List<DataField> fields, List<CollectionError> errors)
    {
        try
        {
            if (_platform.BrowserData.DetectProfiles() is Available<IReadOnlyList<BrowserProfile>> profiles)
            {
                fields.Add(new DataField("browserProfileCount", profiles.Value.Count, "browser"));
                var browsers = profiles.Value
                    .Select(p => p.BrowserName)
                    .Distinct()
                    .ToArray();
                fields.Add(new DataField("detectedBrowsers", string.Join(", ", browsers), "browser"));
            }
        }
        catch (Exception ex) { errors.Add(new CollectionError("browser", ex)); }
    }
}
