using RealTimeTranslater.App.Capture;
using RealTimeTranslater.Core.Models;

namespace RealTimeTranslater.App.TextSources;

internal static class BrowserCompanionRegionMapper
{
    internal static IReadOnlyList<TextRegion> Map(
        BrowserCompanionSnapshot snapshot,
        CaptureFrame frame)
    {
        var outerWidth =
            Math.Max(
                snapshot.OuterWidth,
                snapshot.InnerWidth);

        var outerHeight =
            Math.Max(
                snapshot.OuterHeight,
                snapshot.InnerHeight);

        if (outerWidth <= 0 ||
            outerHeight <= 0)
        {
            return Array.Empty<TextRegion>();
        }

        var scaleX =
            frame.Bitmap.Width /
            (double)outerWidth;
        var scaleY =
            frame.Bitmap.Height /
            (double)outerHeight;

        var browserChromeHeight =
            Math.Max(
                0,
                outerHeight -
                snapshot.InnerHeight);

        var sourceRegions =
            snapshot.Regions
                .Where(region =>
                    !string.IsNullOrWhiteSpace(
                        region.Text))
                .ToArray();

        if (string.Equals(
                snapshot.Kind,
                "youtube-captions",
                StringComparison.OrdinalIgnoreCase) &&
            sourceRegions.Length > 1)
        {
            var ordered =
                sourceRegions
                    .OrderBy(region => region.Y)
                    .ThenBy(region => region.X)
                    .ToArray();

            sourceRegions =
                new[]
                {
                    new BrowserCompanionRegion
                    {
                        Text = string.Join(
                            " ",
                            ordered
                                .Select(region =>
                                    region.Text.Trim())
                                .Where(text =>
                                    text.Length > 0)),
                        Role = "caption",
                        X = ordered.Min(region => region.X),
                        Y = ordered.Min(region => region.Y),
                        Width =
                            ordered.Max(region =>
                                region.X + region.Width) -
                            ordered.Min(region => region.X),
                        Height =
                            ordered.Max(region =>
                                region.Y + region.Height) -
                            ordered.Min(region => region.Y),
                        Partial = ordered.Any(region =>
                            region.Partial)
                    }
                };
        }

        return sourceRegions
            .Select(region =>
            {
                var x =
                    (int)Math.Round(
                        region.X * scaleX);
                var y =
                    (int)Math.Round(
                        (browserChromeHeight +
                         region.Y) *
                        scaleY);
                var width =
                    Math.Max(
                        1,
                        (int)Math.Round(
                            region.Width *
                            scaleX));
                var height =
                    Math.Max(
                        1,
                        (int)Math.Round(
                            region.Height *
                            scaleY));

                x = Math.Clamp(
                    x,
                    0,
                    Math.Max(
                        0,
                        frame.Bitmap.Width - 1));
                y = Math.Clamp(
                    y,
                    0,
                    Math.Max(
                        0,
                        frame.Bitmap.Height - 1));
                width = Math.Clamp(
                    width,
                    1,
                    Math.Max(
                        1,
                        frame.Bitmap.Width - x));
                height = Math.Clamp(
                    height,
                    1,
                    Math.Max(
                        1,
                        frame.Bitmap.Height - y));

                return new TextRegion(
                    region.Text.Trim(),
                    new PixelRect(
                        x,
                        y,
                        width,
                        height),
                    Confidence:
                        string.Equals(
                            snapshot.Kind,
                            "youtube-captions",
                            StringComparison.OrdinalIgnoreCase)
                            ? 100f
                            : 95f);
            })
            .ToArray();
    }
}
