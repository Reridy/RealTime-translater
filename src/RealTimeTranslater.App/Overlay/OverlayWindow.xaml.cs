using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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
        var fontSize = Math.Clamp(
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
            VerticalAlignment = VerticalAlignment.Center
        };

        var alpha = (byte)Math.Clamp(
            _settings.BackgroundOpacity * 255.0,
            0,
            255);

        var border = new Border
        {
            Width = width,
            MinHeight = height,
            MaxHeight = Math.Max(height * 2.4, height + 4),
            Padding = new Thickness(3, 1, 3, 1),
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(
                Color.FromArgb(alpha, 12, 12, 12)),
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

        var text = string.Join(
            "\n",
            regions
                .Select(x => x.TranslatedText.Trim())
                .Where(x => x.Length > 0)
                .TakeLast(6));

        if (text.Length == 0)
            return;

        var alpha = (byte)Math.Clamp(
            _settings.BackgroundOpacity * 255.0,
            0,
            255);

        var textBlock = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = Math.Clamp(
                22 * _settings.FontSizeScale,
                _settings.MinimumFontSize,
                _settings.MaximumFontSize),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        };

        var border = new Border
        {
            Width = Math.Max(320, Width * 0.8),
            Padding = new Thickness(14, 8, 14, 8),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(
                Color.FromArgb(alpha, 10, 10, 10)),
            Child = textBlock,
            IsHitTestVisible = false
        };

        border.Measure(new Size(border.Width, double.PositiveInfinity));
        var desiredHeight = border.DesiredSize.Height;

        Canvas.SetLeft(border, Math.Max(0, (Width - border.Width) / 2));
        Canvas.SetTop(border, Math.Max(0, Height - desiredHeight - 28));
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

        _ = NativeMethods.SetWindowDisplayAffinity(
            handle,
            NativeMethods.WdaExcludeFromCapture);
    }
}
