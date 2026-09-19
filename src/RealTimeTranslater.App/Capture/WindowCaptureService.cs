using System.Drawing;
using System.Drawing.Imaging;
using RealTimeTranslater.App.Interop;
using RealTimeTranslater.Core.Models;

namespace RealTimeTranslater.App.Capture;

public sealed class WindowCaptureService
{
    public CaptureFrame? Capture(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero || NativeMethods.IsIconic(targetWindow))
            return null;

        if (!NativeMethods.GetClientRect(targetWindow, out var rect))
            return null;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 2 || height < 2)
            return null;

        var origin = new NativeMethods.Point { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(targetWindow, ref origin))
            return null;

        try
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    origin.X,
                    origin.Y,
                    0,
                    0,
                    new Size(width, height),
                    CopyPixelOperation.SourceCopy);
            }

            var dpi = NativeMethods.GetDpiForWindow(targetWindow);
            var scale = dpi == 0 ? 1.0 : dpi / 96.0;

            return new CaptureFrame(
                bitmap,
                new PixelRect(origin.X, origin.Y, width, height),
                scale);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
