using System.Text.Json;

namespace DevMX.App.ViewModels;

/// <summary>
/// Application settings persisted to %LOCALAPPDATA%\DevMX\settings.json.
/// </summary>
public sealed record DevMxSettings
{
    public const string DefaultServerExe = @"C:\Users\pkailas\source\repos\DevMind\dist\mcp\DevMind.McpServer.exe";
    public const string DefaultWorkDir = @"C:\Users\pkailas\source\repos\DevMX";
    public const string DefaultEndpoint = "http://127.0.0.1:8080/v1";
    public const string DefaultVisionEndpoint = "http://127.0.0.1:8084/v1";
    public const string DefaultVisionModel = "Qwen3-VL-32B-Instruct-AWQ";

    public string ServerExe { get; set; } = DefaultServerExe;
    public string WorkDir { get; set; } = DefaultWorkDir;
    public string Endpoint { get; set; } = DefaultEndpoint;
    public string Model { get; set; } = string.Empty;

    /// <summary>Vision sidecar endpoint used to OCR image attachments before sending
    /// (same server DevMind's /library add uses). Empty disables the sidecar.</summary>
    public string VisionEndpoint { get; set; } = DefaultVisionEndpoint;
    public string VisionModel { get; set; } = DefaultVisionModel;
    public string Provider { get; set; } = "openai";
    public string Theme { get; set; } = "dark";
    public string ToolProfile { get; set; } = "auto";
    public int PollThrottleSeconds { get; set; } = 5;

    /// <summary>Auto-compact the conversation when its history exceeds roughly this many
    /// tokens: older turns get summarized into a digest. 0 disables compaction.</summary>
    public int CompactThresholdTokens { get; set; } = 100_000;
    public int FontSize { get; set; } = 13;

    public static string DefaultSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevMX", "settings.json");

    public static DevMxSettings Load()
    {
        return Load(DefaultSettingsPath);
    }

    public static DevMxSettings Load(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<DevMxSettings>(json, Options);
                if (settings != null)
                    return settings;
            }
            catch
            {
                // Fall through to defaults
            }
        }
        return new DevMxSettings();
    }

    public void Save()
    {
        Save(DefaultSettingsPath);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, Options);
        File.WriteAllText(path, json);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
