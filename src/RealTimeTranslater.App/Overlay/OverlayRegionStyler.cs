using System.Drawing;
using RealTimeTranslater.App.Capture;
using RealTimeTranslater.Core.Models;

namespace RealTimeTranslater.App.Overlay;

internal static class OverlayRegionStyler
{
    internal static IReadOnlyList<TranslatedRegion> ApplyBackgroundSamples(
        IReadOnlyList<TranslatedRegion> regions,
        CaptureFrame frame)
    {
        if (regions.Count == 0)
            return regions;

        return regions
            .Select(region => region with
            {
                BackgroundArgb = SampleBackgroundArgb(
                    frame.Bitmap,
                    region.Bounds)
            })
            .ToArray();
    }

    private static int? SampleBackgroundArgb(
        Bitmap bitmap,
        PixelRect bounds)
    {
        if (bitmap.Width < 2 ||
            bitmap.Height < 2)
        {
            return null;
        }

        var samples = new List<Color>(160);

        var left = Math.Clamp(
            bounds.X,
            0,
            bitmap.Width - 1);
        var top = Math.Clamp(
            bounds.Y,
            0,
            bitmap.Height - 1);
        var right = Math.Clamp(
            bounds.X + bounds.Width - 1,
            0,
            bitmap.Width - 1);
        var bottom = Math.Clamp(
            bounds.Y + bounds.Height - 1,
            0,
            bitmap.Height - 1);

        var xStep = Math.Max(
            1,
            bounds.Width / 28);
        var yStep = Math.Max(
            1,
            bounds.Height / 12);

        for (var offset = 2; offset <= 8; offset += 3)
        {
            var above = top - offset;
            var below = bottom + offset;

            if (above >= 0)
            {
                for (var x = left;
                     x <= right;
                     x += xStep)
                {
                    samples.Add(
                        bitmap.GetPixel(x, above));
                }
            }

            if (below < bitmap.Height)
            {
                for (var x = left;
                     x <= right;
                     x += xStep)
                {
                    samples.Add(
                        bitmap.GetPixel(x, below));
                }
            }

            var before = left - offset;
            var after = right + offset;

            if (before >= 0)
            {
                for (var y = top;
                     y <= bottom;
                     y += yStep)
                {
                    samples.Add(
                        bitmap.GetPixel(before, y));
                }
            }

            if (after < bitmap.Width)
            {
                for (var y = top;
                     y <= bottom;
                     y += yStep)
                {
                    samples.Add(
                        bitmap.GetPixel(after, y));
                }
            }
        }

        if (samples.Count < 8)
            return null;

        var winningBucket = samples
            .GroupBy(color =>
                ((color.R >> 4) << 8) |
                ((color.G >> 4) << 4) |
                (color.B >> 4))
            .OrderByDescending(group =>
                group.Count())
            .ThenBy(group =>
                group.Key)
            .First();

        var selected =
            winningBucket.ToArray();

        var red = (int)Math.Round(
            selected.Average(color => color.R));
        var green = (int)Math.Round(
            selected.Average(color => color.G));
        var blue = (int)Math.Round(
            selected.Average(color => color.B));

        return Color.FromArgb(
            255,
            red,
            green,
            blue).ToArgb();
    }
}
