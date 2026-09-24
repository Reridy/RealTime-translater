using RealTimeTranslater.App.Capture;
using RealTimeTranslater.Core.Models;

namespace RealTimeTranslater.App.TextSources;

internal static class UnityAdapterRegionMapper
{
    internal static IReadOnlyList<TextRegion> Map(
        UnityAdapterSnapshot snapshot,
        IReadOnlyList<UnityAdapterRegionDto> selectedRegions,
        CaptureFrame frame)
    {
        var sourceWidth = snapshot.Data.ScreenWidth;
        var sourceHeight = snapshot.Data.ScreenHeight;

        if (sourceWidth < 1 || sourceHeight < 1)
            return Array.Empty<TextRegion>();

        var targetWidth = frame.Bitmap.Width;
        var targetHeight = frame.Bitmap.Height;

        if (targetWidth < 1 || targetHeight < 1)
            return Array.Empty<TextRegion>();

        var scaleX = targetWidth / (double)sourceWidth;
        var scaleY = targetHeight / (double)sourceHeight;

        var result = new List<TextRegion>(
            Math.Min(selectedRegions.Count, 128));

        foreach (var source in selectedRegions.Take(128))
        {
            var text = source.Text?.Trim();

            if (string.IsNullOrWhiteSpace(text))
                continue;

            var bounds =
                ScaleAndClamp(
                    source.X,
                    source.Y,
                    source.Width,
                    source.Height,
                    scaleX,
                    scaleY,
                    targetWidth,
                    targetHeight);

            PixelRect? layoutBounds = null;

            if (snapshot.Data.Protocol >= 2 &&
                source.LayoutWidth > 1 &&
                source.LayoutHeight > 1)
            {
                layoutBounds =
                    ScaleAndClamp(
                        source.LayoutX,
                        source.LayoutY,
                        source.LayoutWidth,
                        source.LayoutHeight,
                        scaleX,
                        scaleY,
                        targetWidth,
                        targetHeight);
            }

            result.Add(
                new TextRegion(
                    text,
                    bounds,
                    100f,
                    LayoutBounds: layoutBounds,
                    ForegroundArgb:
                        snapshot.Data.Protocol >= 2
                            ? source.ForegroundArgb
                            : null,
                    SourceLineCount:
                        snapshot.Data.Protocol >= 2 &&
                        source.SourceLineCount > 0
                            ? source.SourceLineCount
                            : null,
                    SourceAlignment:
                        snapshot.Data.Protocol >= 2 &&
                        !string.IsNullOrWhiteSpace(
                            source.SourceAlignment)
                            ? source.SourceAlignment
                            : null));
        }

        return result;
    }

    private static PixelRect ScaleAndClamp(
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        double scaleX,
        double scaleY,
        int targetWidth,
        int targetHeight)
    {
        var x =
            (int)Math.Round(
                sourceX * scaleX);

        var y =
            (int)Math.Round(
                sourceY * scaleY);

        var width =
            (int)Math.Round(
                sourceWidth * scaleX);

        var height =
            (int)Math.Round(
                sourceHeight * scaleY);

        x = Math.Clamp(
            x,
            0,
            Math.Max(
                0,
                targetWidth - 1));

        y = Math.Clamp(
            y,
            0,
            Math.Max(
                0,
                targetHeight - 1));

        width = Math.Clamp(
            width,
            1,
            Math.Max(
                1,
                targetWidth - x));

        height = Math.Clamp(
            height,
            1,
            Math.Max(
                1,
                targetHeight - y));

        return new PixelRect(
            x,
            y,
            width,
            height);
    }
}
