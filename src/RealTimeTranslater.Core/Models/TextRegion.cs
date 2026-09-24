namespace RealTimeTranslater.Core.Models;

public sealed record TextRegion(
    string Text,
    PixelRect Bounds,
    float Confidence = 100f,
    PixelRect? LayoutBounds = null,
    int? ForegroundArgb = null,
    int? SourceLineCount = null,
    string? SourceAlignment = null);
