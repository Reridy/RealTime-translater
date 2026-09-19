using System.Text.Json;

namespace RealTimeTranslater.App.Configuration;

public sealed class AppSettings
{
    public int CaptureFps { get; set; } = 4;
    public double ChangeThreshold { get; set; } = 0.025;
    public int StabilityFrames { get; set; } = 2;
    public string OcrLanguage { get; set; } = "jpn+eng";
    public string OcrDataPath { get; set; } = "tessdata";
    public float MinimumOcrConfidence { get; set; } = 45f;
    public TranslationSettings Translation { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();

    public static AppSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "settings.json");
        if (!File.Exists(path))
            return new AppSettings();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppSettings>(
                   json,
                   new JsonSerializerOptions
                   {
                       PropertyNameCaseInsensitive = true
                   })
               ?? new AppSettings();
    }
}

public sealed class TranslationSettings
{
    public string Provider { get; set; } = "Mock";
    public string SourceLanguage { get; set; } = "ja";
    public string TargetLanguage { get; set; } = "ko";
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "qwen2.5:7b";
    public string LibreTranslateEndpoint { get; set; } = "http://localhost:5000";
}

public sealed class OverlaySettings
{
    public string Mode { get; set; } = "Replace";
    public double BackgroundOpacity { get; set; } = 0.78;
    public double FontSizeScale { get; set; } = 0.82;
    public double MinimumFontSize { get; set; } = 12;
    public double MaximumFontSize { get; set; } = 38;
}
