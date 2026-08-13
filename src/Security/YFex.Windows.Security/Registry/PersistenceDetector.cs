using MsReg = Microsoft.Win32;

namespace YFex.Windows.Security.Registry;

public sealed class PersistenceDetector : IPersistenceDetector
{
    private static readonly (MsReg.RegistryKey Root, string Path, string Description)[] AutostartRegistryKeys =
    [
        (MsReg.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKLM Run"),
        (MsReg.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM RunOnce"),
        (MsReg.Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKCU Run"),
        (MsReg.Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "HKCU RunOnce"),
        (MsReg.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "HKLM Run (WOW64)"),
        (MsReg.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM RunOnce (WOW64)"),
    ];

    public IReadOnlyList<AutostartEntry> EnumerateAll()
    {
        var entries = new List<AutostartEntry>();
        ScanRegistryAutostart(entries);
        ScanWinlogon(entries);
        ScanImageFileExecutionOptions(entries);
        ScanStartupFolders(entries);
        ScanServices(entries);
        ScanScheduledTasks(entries);
        return entries;
    }

    public IReadOnlyList<AutostartEntry> ScanForNew(IReadOnlyList<AutostartEntry> baseline)
    {
        var current = EnumerateAll();
        var baselineSet = new HashSet<string>(
            baseline.Select(e => $"{e.Location}|{e.Name}|{e.Value}"));
        return current.Where(e => !baselineSet.Contains($"{e.Location}|{e.Name}|{e.Value}")).ToList();
    }

    private static void ScanRegistryAutostart(List<AutostartEntry> entries)
    {
        foreach (var (root, path, description) in AutostartRegistryKeys)
        {
            try
            {
                using var key = root.OpenSubKey(path, false);
                if (key is null) continue;

                foreach (var name in key.GetValueNames())
                {
                    string? value = key.GetValue(name)?.ToString();
                    if (string.IsNullOrEmpty(value)) continue;

                    int risk = ScoreRegistryEntry(value, path);
                    entries.Add(new AutostartEntry(description, "Registry", name, value, risk));
                }
            }
            catch { }
        }
    }

    private static void ScanWinlogon(List<AutostartEntry> entries)
    {
        string[] interestingValues = ["Shell", "Userinit", "Notify"];

        try
        {
            using var key = MsReg.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", false);
            if (key is null) return;

            foreach (var name in interestingValues)
            {
                string? value = key.GetValue(name)?.ToString();
                if (string.IsNullOrEmpty(value)) continue;

                int risk = name switch
                {
                    "Shell" when !value.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) => 80,
                    "Userinit" when !value.Contains("userinit.exe", StringComparison.OrdinalIgnoreCase) => 80,
                    _ => 10
                };

                entries.Add(new AutostartEntry("Winlogon", "Registry", name, value, risk));
            }
        }
        catch { }
    }

    private static void ScanImageFileExecutionOptions(List<AutostartEntry> entries)
    {
        try
        {
            using var ifeoKey = MsReg.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options", false);
            if (ifeoKey is null) return;

            foreach (var subKeyName in ifeoKey.GetSubKeyNames())
            {
                try
                {
                    using var subKey = ifeoKey.OpenSubKey(subKeyName, false);
                    string? debugger = subKey?.GetValue("Debugger")?.ToString();
                    if (string.IsNullOrEmpty(debugger)) continue;

                    entries.Add(new AutostartEntry(
                        "IFEO Debugger",
                        "Registry",
                        subKeyName,
                        debugger,
                        70));
                }
                catch { }
            }
        }
        catch { }
    }

    private static void ScanStartupFolders(List<AutostartEntry> entries)
    {
        string[] startupPaths =
        [
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs\Startup"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
        ];

        foreach (var folder in startupPaths)
        {
            if (!Directory.Exists(folder)) continue;

            try
            {
                foreach (var file in Directory.GetFiles(folder))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    int risk = ext switch
                    {
                        ".exe" or ".bat" or ".cmd" or ".vbs" or ".ps1" => 40,
                        ".lnk" => 15,
                        _ => 5
                    };

                    entries.Add(new AutostartEntry(
                        folder.Contains("Common") ? "All Users Startup" : "User Startup",
                        "FileSystem",
                        Path.GetFileName(file),
                        file,
                        risk));
                }
            }
            catch { }
        }
    }

    private static void ScanServices(List<AutostartEntry> entries)
    {
        try
        {
            using var servicesKey = MsReg.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services", false);
            if (servicesKey is null) return;

            foreach (var serviceName in servicesKey.GetSubKeyNames())
            {
                try
                {
                    using var svcKey = servicesKey.OpenSubKey(serviceName, false);
                    if (svcKey is null) continue;

                    var startValue = svcKey.GetValue("Start");
                    if (startValue is not int start || start > 2) continue;

                    string? imagePath = svcKey.GetValue("ImagePath")?.ToString();
                    if (string.IsNullOrEmpty(imagePath)) continue;

                    if (imagePath.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)
                        && !imagePath.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int risk = start == 0 ? 30 : 20;
                    if (!imagePath.Contains(@"\Program Files", StringComparison.OrdinalIgnoreCase))
                        risk += 20;

                    entries.Add(new AutostartEntry(
                        "Service",
                        "Service",
                        serviceName,
                        imagePath,
                        risk));
                }
                catch { }
            }
        }
        catch { }
    }

    private static void ScanScheduledTasks(List<AutostartEntry> entries)
    {
        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/query /fo CSV /nh /v",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = global::System.Diagnostics.Process.Start(psi);
            if (process is null) return;

            using var reader = process.StandardOutput;
            while (reader.ReadLine() is { } line)
            {
                var parts = line.Split(',');
                if (parts.Length < 9) continue;

                string taskName = parts[0].Trim('"');
                string action = parts.Length > 8 ? parts[8].Trim('"') : "";

                if (taskName.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(action) || action == "N/A") continue;

                int risk = 25;
                if (action.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)
                    || action.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase))
                    risk = 50;

                entries.Add(new AutostartEntry(
                    "Scheduled Task",
                    "ScheduledTask",
                    taskName,
                    action,
                    risk));
            }

            process.WaitForExit(5000);
        }
        catch { }
    }

    private static int ScoreRegistryEntry(string value, string keyPath)
    {
        int score = 15;
        var lower = value.ToLowerInvariant();

        if (lower.Contains("\\temp\\") || lower.Contains("\\tmp\\"))
            score += 35;
        if (lower.Contains("\\appdata\\"))
            score += 15;
        if (lower.Contains("powershell") || lower.Contains("cmd.exe") || lower.Contains("wscript"))
            score += 20;
        if (lower.Contains("-encodedcommand") || lower.Contains("-enc "))
            score += 40;
        if (keyPath.Contains("RunOnce"))
            score += 10;

        return Math.Min(score, 100);
    }
}
