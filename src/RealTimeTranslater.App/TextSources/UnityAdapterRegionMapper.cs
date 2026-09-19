using RealTimeTranslater.App.Capture;
using RealTimeTranslater.Core.Models;

namespace RealTimeTranslater.App.TextSources;

internal static class UnityAdapterRegionMapper
{
    internal static IReadOnlyList<TextRegion> Map(
        UnityAdapterSnapshot snapshot,
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
            Math.Min(snapshot.Data.Regions.Count, 128));

        foreach (var source in snapshot.Data.Regions.Take(128))
        {
            var text = source.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var x = (int)Math.Round(source.X * scaleX);
            var y = (int)Math.Round(source.Y * scaleY);
            var width = (int)Math.Round(source.Width * scaleX);
            var height = (int)Math.Round(source.Height * scaleY);

            x = Math.Clamp(x, 0, Math.Max(0, targetWidth - 1));
            y = Math.Clamp(y, 0, Math.Max(0, targetHeight - 1));
            width = Math.Clamp(width, 1, Math.Max(1, targetWidth - x));
            height = Math.Clamp(height, 1, Math.Max(1, targetHeight - y));

            result.Add(new TextRegion(
                text,
                new PixelRect(x, y, width, height),
                100f));
        }

        return result;
    }
}
