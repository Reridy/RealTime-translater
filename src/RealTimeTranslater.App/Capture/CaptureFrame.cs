using System.Drawing;
using RealTimeTranslater.Core.Models;

namespace RealTimeTranslater.App.Capture;

public sealed class CaptureFrame : IDisposable
{
    public CaptureFrame(Bitmap bitmap, PixelRect screenBounds, double dpiScale)
    {
        Bitmap = bitmap;
        ScreenBounds = screenBounds;
        DpiScale = dpiScale;
    }

    public Bitmap Bitmap { get; }
    public PixelRect ScreenBounds { get; }
    public double DpiScale { get; }

    public void Dispose() => Bitmap.Dispose();
}
