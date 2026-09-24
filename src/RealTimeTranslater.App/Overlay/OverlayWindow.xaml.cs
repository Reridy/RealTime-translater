using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using RealTimeTranslater.App.Configuration;
using RealTimeTranslater.App.Interop;
using RealTimeTranslater.Core.Models;

namespace RealTimeTranslater.App.Overlay;

public partial class OverlayWindow : Window
{
    private readonly OverlaySettings _settings;
    private readonly bool _rightToLeftTarget;

    public OverlayWindow(
        OverlaySettings settings,
        string targetLanguage = "ko")
    {
        _settings = settings;
        _rightToLeftTarget =
            IsRightToLeftLanguage(
                targetLanguage);
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    public void Render(
        IReadOnlyList<TranslatedRegion> regions,
        PixelRect screenBounds,
        double dpiScale)
    {
        dpiScale = dpiScale <= 0 ? 1.0 : dpiScale;

        Left = screenBounds.X / dpiScale;
        Top = screenBounds.Y / dpiScale;
        Width = Math.Max(1, screenBounds.Width / dpiScale);
        Height = Math.Max(1, screenBounds.Height / dpiScale);

        OverlayCanvas.Children.Clear();

        if (string.Equals(
                _settings.Mode,
                "Subtitle",
                StringComparison.OrdinalIgnoreCase))
        {
            RenderSubtitle(regions, dpiScale);
            return;
        }

        if (string.Equals(
                _settings.Mode,
                "Smart",
                StringComparison.OrdinalIgnoreCase))
        {
            var subtitleRegions = regions
                .Where(region =>
                    ShouldRenderSmartSubtitle(
                        region,
                        dpiScale))
                .ToArray();

            var subtitleSet =
                subtitleRegions.ToHashSet();

            foreach (var region in regions)
            {
                if (subtitleSet.Contains(region))
                {
                    RenderSourceMask(
                        region,
                        dpiScale);
                }
                else
                {
                    RenderReplaceRegion(
                        region,
                        dpiScale);
                }
            }

            if (subtitleRegions.Length > 0)
            {
                RenderSubtitle(
                    subtitleRegions,
                    dpiScale);
            }

            return;
        }

        foreach (var region in regions)
            RenderReplaceRegion(region, dpiScale);
    }

    private void RenderReplaceRegion(
        TranslatedRegion region,
        double dpiScale)
    {
        var sourceText =
            region.OriginalText?.Trim() ??
            string.Empty;

        var translatedText =
            LimitOverlayText(
                region.TranslatedText,
                maxCharacters: 520,
                maxLines: 8);

        if (translatedText.Length == 0)
            return;

        var sourceLeft =
            region.Bounds.X / dpiScale;
        var sourceTop =
            region.Bounds.Y / dpiScale;
        var sourceWidth =
            Math.Max(
                2,
                region.Bounds.Width / dpiScale);
        var sourceHeight =
            Math.Max(
                2,
                region.Bounds.Height / dpiScale);

        var layout =
            region.LayoutBounds ??
            region.Bounds;

        var layoutLeft =
            layout.X / dpiScale;
        var layoutTop =
            layout.Y / dpiScale;
        var layoutWidth =
            Math.Max(
                sourceWidth,
                layout.Width / dpiScale);
        var layoutHeight =
            Math.Max(
                sourceHeight,
                layout.Height / dpiScale);

        layoutLeft = Math.Clamp(
            layoutLeft,
            0,
            Math.Max(
                0,
                Width - 1));

        layoutTop = Math.Clamp(
            layoutTop,
            0,
            Math.Max(
                0,
                Height - 1));

        layoutWidth = Math.Clamp(
            layoutWidth,
            12,
            Math.Max(
                12,
                Width - layoutLeft));

        layoutHeight = Math.Clamp(
            layoutHeight,
            12,
            Math.Max(
                12,
                Height - layoutTop));

        var replaceBackground =
            ResolveReplaceBackground(
                region.BackgroundArgb);

        var replaceForeground =
            ResolveReplaceForeground(
                region.ForegroundArgb,
                replaceBackground.Color);

        RenderSourceMask(
            region,
            dpiScale);

        var sourceLineCount =
            Math.Max(
                1,
                region.SourceLineCount ??
                (sourceText.Count(ch => ch == '\n') + 1));

        var lineHeightEstimate =
            sourceHeight /
            sourceLineCount;

        var minimumFontSize =
            Math.Min(
                _settings.MinimumFontSize,
                10.5);

        var preferredFontSize =
            Math.Clamp(
                lineHeightEstimate * 0.88,
                minimumFontSize,
                Math.Min(
                    _settings.MaximumFontSize,
                    30));

        var horizontalPadding =
            Math.Clamp(
                preferredFontSize * 0.12,
                1.5,
                4.0);

        var verticalPadding =
            Math.Clamp(
                preferredFontSize * 0.06,
                1.0,
                3.0);

        var textAlignment =
            ResolveTextAlignment(
                region.SourceAlignment,
                sourceText,
                layoutWidth);

        if (_rightToLeftTarget &&
            textAlignment ==
                TextAlignment.Left)
        {
            textAlignment =
                TextAlignment.Right;
        }

        var verticalAlignment =
            ResolveVerticalAlignment(
                region.SourceAlignment);

        var availableWidth =
            Math.Max(
                10,
                layoutWidth -
                horizontalPadding * 2);

        var availableHeight =
            Math.Max(
                10,
                layoutHeight -
                verticalPadding * 2);

        var fontSize =
            FitFontSize(
                translatedText,
                preferredFontSize,
                minimumFontSize,
                availableWidth,
                availableHeight,
                topAligned:
                    textAlignment ==
                    TextAlignment.Left);

        var textBlock = new TextBlock
        {
            Text = translatedText,
            Foreground = replaceForeground,
            FlowDirection = _rightToLeftTarget
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight,
            FontWeight = FontWeights.Normal,
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
            TextAlignment = textAlignment,
            VerticalAlignment = verticalAlignment,
            LineStackingStrategy =
                LineStackingStrategy.BlockLineHeight,
            LineHeight = fontSize * 1.16,
            IsHitTestVisible = false
        };

        var textContainer = new Border
        {
            Width = layoutWidth,
            Height = layoutHeight,
            Padding = new Thickness(
                horizontalPadding,
                verticalPadding,
                horizontalPadding,
                verticalPadding),
            Background = Brushes.Transparent,
            Child = textBlock,
            IsHitTestVisible = false,
            ClipToBounds = true,
            SnapsToDevicePixels = true
        };

        Canvas.SetLeft(
            textContainer,
            layoutLeft);

        Canvas.SetTop(
            textContainer,
            layoutTop);

        OverlayCanvas.Children.Add(
            textContainer);
    }

    private bool ShouldRenderSmartSubtitle(
        TranslatedRegion region,
        double dpiScale)
    {
        var sourceText =
            region.OriginalText?.Trim() ??
            string.Empty;

        var translatedText =
            region.TranslatedText?.Trim() ??
            string.Empty;

        var sourceWidth =
            region.Bounds.Width /
            Math.Max(
                0.1,
                dpiScale);

        var sourceHeight =
            region.Bounds.Height /
            Math.Max(
                0.1,
                dpiScale);

        var sourceLineCount =
            Math.Max(
                1,
                region.SourceLineCount ??
                (sourceText.Count(ch => ch == '\n') + 1));

        if (region.LayoutBounds is PixelRect layout)
        {
            var layoutWidth =
                layout.Width /
                Math.Max(
                    0.1,
                    dpiScale);

            var layoutHeight =
                layout.Height /
                Math.Max(
                    0.1,
                    dpiScale);

            var hasUsefulLayoutRoom =
                layoutWidth >=
                    sourceWidth * 1.12 &&
                layoutHeight >=
                    Math.Max(
                        sourceHeight * 1.18,
                        34);

            if (hasUsefulLayoutRoom)
            {
                return false;
            }
        }

        var wideDialogue =
            sourceWidth >=
                Width * 0.34;

        var longSource =
            sourceText.Length >= 54 ||
            sourceLineCount >= 2;

        var translationExpands =
            sourceText.Length >= 12 &&
            translatedText.Length >=
                sourceText.Length * 1.40;

        return
            (longSource && wideDialogue) ||
            (translationExpands &&
             sourceWidth >= Width * 0.24);
    }

    private void RenderSourceMask(
        TranslatedRegion region,
        double dpiScale)
    {
        var sourceLeft =
            region.Bounds.X / dpiScale;

        var sourceTop =
            region.Bounds.Y / dpiScale;

        var sourceWidth =
            Math.Max(
                2,
                region.Bounds.Width / dpiScale);

        var sourceHeight =
            Math.Max(
                2,
                region.Bounds.Height / dpiScale);

        var maskPaddingX = 2.5;
        var maskPaddingY = 2.0;

        var maskWidth =
            Math.Min(
                Width,
                sourceWidth +
                maskPaddingX * 2);

        var maskHeight =
            Math.Min(
                Height,
                sourceHeight +
                maskPaddingY * 2);

        var maskLeft =
            Math.Clamp(
                sourceLeft -
                maskPaddingX,
                0,
                Math.Max(
                    0,
                    Width - maskWidth));

        var maskTop =
            Math.Clamp(
                sourceTop -
                maskPaddingY,
                0,
                Math.Max(
                    0,
                    Height - maskHeight));

        var mask = new Border
        {
            Width = maskWidth,
            Height = maskHeight,
            Background =
                ResolveReplaceBackground(
                    region.BackgroundArgb),
            CornerRadius =
                new CornerRadius(0),
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };

        Canvas.SetLeft(
            mask,
            maskLeft);

        Canvas.SetTop(
            mask,
            maskTop);

        OverlayCanvas.Children.Add(
            mask);
    }

    private static SolidColorBrush ResolveReplaceBackground(
        int? backgroundArgb)
    {
        if (backgroundArgb is int argb)
        {
            var value =
                unchecked((uint)argb);

            return new SolidColorBrush(
                Color.FromArgb(
                    255,
                    (byte)((value >> 16) & 0xFF),
                    (byte)((value >> 8) & 0xFF),
                    (byte)(value & 0xFF)));
        }

        return new SolidColorBrush(
            Color.FromArgb(
                255,
                8,
                8,
                8));
    }

    private static Brush ResolveReplaceForeground(
        int? foregroundArgb,
        Color background)
    {
        if (foregroundArgb is int argb)
        {
            var value =
                unchecked((uint)argb);

            var red =
                (byte)((value >> 16) & 0xFF);
            var green =
                (byte)((value >> 8) & 0xFF);
            var blue =
                (byte)(value & 0xFF);

            var sourceLuminance =
                (
                    0.2126 * red +
                    0.7152 * green +
                    0.0722 * blue
                ) / 255.0;

            var backgroundLuminance =
                (
                    0.2126 * background.R +
                    0.7152 * background.G +
                    0.0722 * background.B
                ) / 255.0;

            if (Math.Abs(
                    sourceLuminance -
                    backgroundLuminance) >=
                0.28)
            {
                return new SolidColorBrush(
                    Color.FromRgb(
                        red,
                        green,
                        blue));
            }
        }

        var luminance =
            (
                0.2126 * background.R +
                0.7152 * background.G +
                0.0722 * background.B
            ) / 255.0;

        return luminance >= 0.60
            ? new SolidColorBrush(
                Color.FromRgb(
                    22,
                    22,
                    22))
            : Brushes.White;
    }

    private static TextAlignment ResolveTextAlignment(
        string? sourceAlignment,
        string sourceText,
        double layoutWidth)
    {
        if (!string.IsNullOrWhiteSpace(
                sourceAlignment))
        {
            if (sourceAlignment.Contains(
                    "Right",
                    StringComparison.OrdinalIgnoreCase))
            {
                return TextAlignment.Right;
            }

            if (sourceAlignment.Contains(
                    "Center",
                    StringComparison.OrdinalIgnoreCase) ||
                sourceAlignment.Contains(
                    "Midline",
                    StringComparison.OrdinalIgnoreCase) &&
                !sourceAlignment.Contains(
                    "Left",
                    StringComparison.OrdinalIgnoreCase) &&
                !sourceAlignment.Contains(
                    "Right",
                    StringComparison.OrdinalIgnoreCase))
            {
                return TextAlignment.Center;
            }

            if (sourceAlignment.Contains(
                    "Left",
                    StringComparison.OrdinalIgnoreCase))
            {
                return TextAlignment.Left;
            }
        }

        return sourceText.Length >= 24 ||
            layoutWidth >= 320
                ? TextAlignment.Left
                : TextAlignment.Center;
    }

    private static VerticalAlignment ResolveVerticalAlignment(
        string? sourceAlignment)
    {
        if (string.IsNullOrWhiteSpace(
                sourceAlignment))
        {
            return VerticalAlignment.Top;
        }

        if (sourceAlignment.Contains(
                "Bottom",
                StringComparison.OrdinalIgnoreCase))
        {
            return VerticalAlignment.Bottom;
        }

        if (sourceAlignment.Contains(
                "Midline",
                StringComparison.OrdinalIgnoreCase) ||
            sourceAlignment.Equals(
                "Center",
                StringComparison.OrdinalIgnoreCase))
        {
            return VerticalAlignment.Center;
        }

        return VerticalAlignment.Top;
    }

    private static double FitFontSize(
        string text,
        double preferred,
        double minimum,
        double width,
        double height,
        bool topAligned)
    {
        var candidate = Math.Max(
            minimum,
            preferred);

        for (var size = candidate;
             size >= minimum;
             size -= 0.75)
        {
            var probe = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.Normal,
                FontSize = size,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = topAligned
                    ? TextAlignment.Left
                    : TextAlignment.Center,
                LineStackingStrategy =
                    LineStackingStrategy.BlockLineHeight,
                LineHeight = size * 1.20
            };

            probe.Measure(
                new Size(
                    Math.Max(1, width),
                    double.PositiveInfinity));

            if (probe.DesiredSize.Height <= height)
                return size;
        }

        return minimum;
    }

    private void RenderSubtitle(
        IReadOnlyList<TranslatedRegion> regions,
        double dpiScale)
    {
        if (regions.Count == 0)
            return;

        var lines = regions
            .Select(x => x.TranslatedText.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .TakeLast(4)
            .ToArray();

        if (lines.Length == 0)
            return;

        var text = LimitOverlayText(
            string.Join("\n", lines),
            maxCharacters: 360,
            maxLines: 4);

        var maxWidth = Math.Clamp(
            Width * _settings.SubtitleMaxWidthRatio,
            320,
            Math.Max(320, Width - 48));

        var fontSize = Math.Clamp(
            Height * 0.024 * _settings.FontSizeScale,
            _settings.MinimumFontSize,
            Math.Min(_settings.MaximumFontSize, 28));

        var textBlock = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FlowDirection = _rightToLeftTarget
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight,
            FontWeight = FontWeights.SemiBold,
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = fontSize * 1.24,
            MaxWidth = Math.Max(280, maxWidth - 28),
            Effect = new DropShadowEffect
            {
                BlurRadius = 3,
                ShadowDepth = 1,
                Opacity = 0.9
            }
        };

        var alpha = (byte)Math.Clamp(
            _settings.SubtitleBackgroundOpacity * 255.0,
            0,
            255);

        var border = new Border
        {
            MaxWidth = maxWidth,
            MinWidth = Math.Min(280, maxWidth),
            Padding = new Thickness(14, 6, 14, 7),
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(
                Color.FromArgb(alpha, 8, 8, 8)),
            Child = textBlock,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };

        border.Measure(new Size(maxWidth, double.PositiveInfinity));

        var desiredWidth = Math.Min(
            maxWidth,
            Math.Max(border.MinWidth, border.DesiredSize.Width));
        var desiredHeight = border.DesiredSize.Height;

        border.Width = desiredWidth;

        var anchor = regions
            .OrderByDescending(region => region.Bounds.Width)
            .First();

        var anchorLeft = anchor.Bounds.X / dpiScale;
        var anchorTop = anchor.Bounds.Y / dpiScale;
        var anchorWidth = anchor.Bounds.Width / dpiScale;
        var anchorHeight = anchor.Bounds.Height / dpiScale;

        var left = Math.Clamp(
            anchorLeft + (anchorWidth - desiredWidth) / 2,
            12,
            Math.Max(12, Width - desiredWidth - 12));

        var preferredInsideDialogue =
            anchorHeight >= desiredHeight + 8 &&
            anchorWidth >= Width * 0.35;

        var top = preferredInsideDialogue
            ? anchorTop + anchorHeight - desiredHeight - 4
            : Height - desiredHeight - _settings.SubtitleBottomMargin;

        top = Math.Clamp(
            top,
            12,
            Math.Max(12, Height - desiredHeight - 12));

        Canvas.SetLeft(border, left);
        Canvas.SetTop(border, top);
        OverlayCanvas.Children.Add(border);
    }

    private static bool IsRightToLeftLanguage(
        string languageCode)
    {
        var normalized =
            languageCode
                .Trim()
                .ToLowerInvariant();

        var separator =
            normalized.IndexOfAny(
                new[] { '-', '_' });

        if (separator > 0)
            normalized = normalized[..separator];

        return normalized is
            "ar" or
            "fa" or
            "ur" or
            "he";
    }

    private static string LimitOverlayText(
        string text,
        int maxCharacters,
        int maxLines)
    {
        var normalized = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Trim();

        var lines = normalized
            .Split('\n')
            .Take(Math.Max(1, maxLines))
            .ToArray();

        var limited = string.Join("\n", lines);

        if (limited.Length <= maxCharacters)
            return limited;

        return limited[..Math.Max(1, maxCharacters - 1)].TrimEnd() + "…";
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var current = NativeMethods.GetWindowLongPtr(
            handle,
            NativeMethods.GwlExStyle).ToInt64();

        current |=
            NativeMethods.WsExTransparent |
            NativeMethods.WsExToolWindow |
            NativeMethods.WsExNoActivate |
            NativeMethods.WsExLayered;

        NativeMethods.SetWindowLongPtr(
            handle,
            NativeMethods.GwlExStyle,
            new IntPtr(current));

        ApplyCaptureProtection();
    }

    public void SetAllowScreenshots(bool allowScreenshots)
    {
        _settings.AllowScreenshots = allowScreenshots;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        ApplyCaptureProtection();
    }

    private void ApplyCaptureProtection()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        _ = NativeMethods.SetWindowDisplayAffinity(
            handle,
            _settings.AllowScreenshots
                ? NativeMethods.WdaNone
                : NativeMethods.WdaExcludeFromCapture);
    }
}
