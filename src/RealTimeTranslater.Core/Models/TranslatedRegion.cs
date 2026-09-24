namespace RealTimeTranslater.Core.Models;

public sealed record TranslatedRegion(
    string OriginalText,
    string TranslatedText,
    PixelRect Bounds,
    float Confidence = 100f,
    int? BackgroundArgb = null,
    PixelRect? LayoutBounds = null,
    int? ForegroundArgb = null,
    int? SourceLineCount = null,
    string? SourceAlignment = null);
