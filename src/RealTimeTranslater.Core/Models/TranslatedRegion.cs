namespace RealTimeTranslater.Core.Models;

public sealed record TranslatedRegion(
    string OriginalText,
    string TranslatedText,
    PixelRect Bounds,
    float Confidence = 100f);
