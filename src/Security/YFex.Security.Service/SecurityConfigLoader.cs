using System.Text.Json;
using YFex.Security.Config;

namespace YFex.Security.Service;

/// <summary>Loads <see cref="SecurityConfig"/> from a JSON file, falling back to defaults.</summary>
public static class SecurityConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string DefaultConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YFex", "security-config.json");

    public static SecurityConfig Load(string? path = null)
    {
        path ??= DefaultConfigPath;

        if (!File.Exists(path))
            return new SecurityConfig();

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SecurityConfig>(json, Options) ?? new SecurityConfig();
        }
        catch
        {
            return new SecurityConfig();
        }
    }

    public static void Save(SecurityConfig config, string? path = null)
    {
        path ??= DefaultConfigPath;
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
