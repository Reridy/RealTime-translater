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

    public OverlayWindow(OverlaySettings settings)
    {
        _settings = settings;
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

        foreach (var region in regions)
            RenderReplaceRegion(region, dpiScale);
    }

    private void RenderReplaceRegion(
        TranslatedRegion region,
        double dpiScale)
    {
        var sourceWidth = Math.Max(
            16,
            region.Bounds.Width / dpiScale);
        var sourceHeight = Math.Max(
            12,
            region.Bounds.Height / dpiScale);

        var sourceText =
            region.OriginalText?.Trim() ??
            string.Empty;

        var translatedText = LimitOverlayText(
            region.TranslatedText,
            maxCharacters: 520,
            maxLines: 8);

        var sourceLineEstimate = Math.Max(
            1,
            sourceText.Count(ch => ch == '\n') + 1);

        var looksLikeLongContent =
            sourceText.Length >= 42 ||
            sourceLineEstimate >= 2;

        // Tight replace mode deliberately masks only the rendered source
        // glyph area plus a tiny safety margin. The adapter now supplies
        // TextMeshPro's actual text bounds instead of the whole RectTransform.
        var horizontalPadding =
            looksLikeLongContent ? 3.0 : 2.0;
        var verticalPadding =
            looksLikeLongContent ? 2.5 : 1.5;

        var maximumWidth =
            Math.Max(
                20,
                Width - 4);

        var boxWidth = Math.Min(
            sourceWidth +
                horizontalPadding * 2,
            maximumWidth);

        // Prefer shrinking Korean text over expanding the mask into nearby
        // portraits, buttons, or decorative dialogue UI.
        var preferredHeight =
            sourceHeight +
            verticalPadding * 2;

        var maximumHeight =
            looksLikeLongContent
                ? Math.Min(
                    Math.Max(
                        preferredHeight + 4,
                        sourceHeight * 1.10),
                    Height * 0.34)
                : preferredHeight + 2;

        var minimumFontSize =
            Math.Min(
                _settings.MinimumFontSize,
                11.0);

        var estimatedLineHeight =
            sourceHeight /
            Math.Max(
                1,
                sourceLineEstimate);

        var preferredFontSize =
            Math.Clamp(
                estimatedLineHeight * 0.84,
                minimumFontSize,
                Math.Min(
                    _settings.MaximumFontSize,
                    28));

        var availableWidth =
            Math.Max(
                16,
                boxWidth -
                horizontalPadding * 2);

        var availableHeight =
            Math.Max(
                12,
                maximumHeight -
                verticalPadding * 2);

        var fontSize = FitFontSize(
            translatedText,
            preferredFontSize,
            minimumFontSize,
            availableWidth,
            availableHeight,
            topAligned: looksLikeLongContent);

        var textBlock = new TextBlock
        {
            Text = translatedText,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
            TextAlignment = looksLikeLongContent
                ? TextAlignment.Left
                : TextAlignment.Center,
            VerticalAlignment = looksLikeLongContent
                ? VerticalAlignment.Top
                : VerticalAlignment.Center,
            LineStackingStrategy =
                LineStackingStrategy.BlockLineHeight,
            LineHeight = fontSize * 1.17,
            Effect = new DropShadowEffect
            {
                BlurRadius = 2,
                ShadowDepth = 1,
                Opacity = 0.78
            }
        };

        var alpha = (byte)Math.Clamp(
            Math.Max(
                _settings.BackgroundOpacity,
                0.94) *
            255.0,
            0,
            255);

        var border = new Border
        {
            Width = boxWidth,
            MinHeight = preferredHeight,
            MaxHeight = maximumHeight,
            Padding = new Thickness(
                horizontalPadding,
                verticalPadding,
                horizontalPadding,
                verticalPadding),
            CornerRadius =
                new CornerRadius(2),
            Background =
                new SolidColorBrush(
                    Color.FromArgb(
                        alpha,
                        8,
                        8,
                        8)),
            Child = textBlock,
            IsHitTestVisible = false,
            ClipToBounds = true,
            SnapsToDevicePixels = true
        };

        border.Measure(
            new Size(
                boxWidth,
                maximumHeight));

        var measuredHeight = Math.Clamp(
            border.DesiredSize.Height,
            preferredHeight,
            maximumHeight);

        var x =
            region.Bounds.X / dpiScale -
            horizontalPadding;

        var y =
            region.Bounds.Y / dpiScale -
            verticalPadding;

        x = Math.Clamp(
            x,
            2,
            Math.Max(
                2,
                Width - boxWidth - 2));

        y = Math.Clamp(
            y,
            2,
            Math.Max(
                2,
                Height - measuredHeight - 2));

        Canvas.SetLeft(
            border,
            x);
        Canvas.SetTop(
            border,
            y);
        OverlayCanvas.Children.Add(
            border);
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
                FontWeight = FontWeights.SemiBold,
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
