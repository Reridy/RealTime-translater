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
            RenderSubtitle(regions);
            return;
        }

        foreach (var region in regions)
            RenderReplaceRegion(region, dpiScale);
    }

    private void RenderReplaceRegion(
        TranslatedRegion region,
        double dpiScale)
    {
        var width = Math.Max(24, region.Bounds.Width / dpiScale);
        var height = Math.Max(16, region.Bounds.Height / dpiScale);

        var looksLikeDialogueRegion =
            width >= Width * 0.45 &&
            height >= Math.Max(36, Height * 0.055);

        var fontSize = looksLikeDialogueRegion
            ? Math.Clamp(
                height * 0.28,
                Math.Max(14, _settings.MinimumFontSize),
                Math.Min(28, _settings.MaximumFontSize))
            : Math.Clamp(
                height * _settings.FontSizeScale,
                _settings.MinimumFontSize,
                _settings.MaximumFontSize);

        var textBlock = new TextBlock
        {
            Text = region.TranslatedText,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = fontSize * 1.22,
            Effect = looksLikeDialogueRegion
                ? new DropShadowEffect
                {
                    BlurRadius = 3,
                    ShadowDepth = 1,
                    Opacity = 0.85
                }
                : null
        };

        var opacity = looksLikeDialogueRegion
            ? Math.Max(_settings.BackgroundOpacity, 0.93)
            : _settings.BackgroundOpacity;

        var alpha = (byte)Math.Clamp(
            opacity * 255.0,
            0,
            255);

        var border = new Border
        {
            Width = width,
            MinHeight = height,
            MaxHeight = looksLikeDialogueRegion
                ? Math.Max(height * 1.25, height + 8)
                : Math.Max(height * 2.4, height + 4),
            Padding = looksLikeDialogueRegion
                ? new Thickness(8, 4, 8, 5)
                : new Thickness(3, 1, 3, 1),
            CornerRadius = new CornerRadius(
                looksLikeDialogueRegion ? 5 : 3),
            Background = new SolidColorBrush(
                Color.FromArgb(alpha, 10, 10, 10)),
            Child = textBlock,
            IsHitTestVisible = false
        };

        Canvas.SetLeft(border, region.Bounds.X / dpiScale);
        Canvas.SetTop(border, region.Bounds.Y / dpiScale);
        OverlayCanvas.Children.Add(border);
    }

    private void RenderSubtitle(IReadOnlyList<TranslatedRegion> regions)
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

        var text = string.Join("\n", lines);

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

        var left = Math.Max(12, (Width - desiredWidth) / 2);
        var top = Math.Max(
            12,
            Height - desiredHeight - _settings.SubtitleBottomMargin);

        Canvas.SetLeft(border, left);
        Canvas.SetTop(border, top);
        OverlayCanvas.Children.Add(border);
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
