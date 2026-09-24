using System.IO;
using System.Text.Json;

namespace RealTimeTranslater.App.Configuration;

public sealed class AppSettings
{
    public int CaptureFps { get; set; } = 8;
    public double ChangeThreshold { get; set; } = 0.025;
    public int StabilityFrames { get; set; } = 2;
    public string OcrLanguage { get; set; } = "jpn+eng";
    public string OcrRegion { get; set; } = "Full window";
    public string TextSource { get; set; } = "Auto (Recommended)";
    public bool UnityDialogueOnly { get; set; } = true;
    public string OcrDataPath { get; set; } = "tessdata";
    public float MinimumOcrConfidence { get; set; } = 45f;
    public TranslationSettings Translation { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public string LastTargetProfileKey { get; set; } = string.Empty;
    public Dictionary<string, GameProfileSettings> GameProfiles { get; set; } = new();

    public static AppSettings Load()
    {
        foreach (var path in CandidatePaths())
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var json =
                    File.ReadAllText(path);

                var settings =
                    JsonSerializer.Deserialize<AppSettings>(
                        json,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                if (settings is not null)
                    return settings;
            }
            catch
            {
                // A broken user config should not prevent launch.
            }
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var path =
                UserSettingsPath();

            Directory.CreateDirectory(
                Path.GetDirectoryName(path)!);

            var temporary =
                path + ".tmp";

            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(
                    this,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));

            File.Move(
                temporary,
                path,
                overwrite: true);
        }
        catch
        {
            // Settings persistence is non-critical at runtime.
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return UserSettingsPath();
        yield return Path.Combine(
            AppContext.BaseDirectory,
            "settings.json");
    }

    private static string UserSettingsPath()
        => Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "RealTimeTranslater",
            "settings.json");
}

public sealed class TranslationSettings
{
    public string Provider { get; set; } = "Mock";
    public string SourceLanguage { get; set; } = "ja";
    public string TargetLanguage { get; set; } = "ko";
    public string Mode { get; set; } = "Balanced";
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "translategemma:4b";
    public string LibreTranslateEndpoint { get; set; } = "http://localhost:5000";
    public string GlossaryText { get; set; } = string.Empty;
}

public sealed class OverlaySettings
{
    public string Mode { get; set; } = "Smart";
    public double BackgroundOpacity { get; set; } = 0.78;
    public double FontSizeScale { get; set; } = 0.82;
    public double MinimumFontSize { get; set; } = 12;
    public double MaximumFontSize { get; set; } = 38;
    public double SubtitleBackgroundOpacity { get; set; } = 0.58;
    public double SubtitleMaxWidthRatio { get; set; } = 0.74;
    public double SubtitleBottomMargin { get; set; } = 18;
    public bool AllowScreenshots { get; set; } = false;
}

public sealed class GameProfileSettings
{
    public string OcrLanguage { get; set; } = "jpn+eng";
    public string OcrRegion { get; set; } = "Full window";
    public string TextSource { get; set; } = "Auto (Recommended)";
    public bool UnityDialogueOnly { get; set; } = true;
    public string TargetLanguage { get; set; } = "ko";
    public string TranslationMode { get; set; } = "Balanced";
    public string Provider { get; set; } = "Ollama";
    public string OllamaModel { get; set; } = "translategemma:4b";
    public string OverlayMode { get; set; } = "Smart";
    public bool AllowScreenshots { get; set; }
    public string GlossaryText { get; set; } = string.Empty;
}
