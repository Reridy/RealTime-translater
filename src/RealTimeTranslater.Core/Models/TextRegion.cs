namespace RealTimeTranslater.Core.Models;

public sealed record TextRegion(
    string Text,
    PixelRect Bounds,
    float Confidence = 100f);
