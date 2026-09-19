using System.Drawing;
using System.Drawing.Imaging;
using RealTimeTranslater.App.Interop;
using RealTimeTranslater.Core.Models;

namespace RealTimeTranslater.App.Capture;

public sealed class WindowCaptureService : IDisposable
{
    private WindowsGraphicsCaptureBackend? _windowsGraphicsCapture;
    private IntPtr _captureTarget;
    private bool _wgcUnavailable;
    private string? _wgcFailureReason;

    public string BackendName =>
        _windowsGraphicsCapture is not null
            ? "Windows Graphics Capture"
            : _wgcUnavailable
                ? "GDI fallback"
                : "Windows Graphics Capture (initializing)";

    public string? FallbackReason => _wgcFailureReason;

    public async Task<CaptureFrame?> CaptureAsync(
        IntPtr targetWindow,
        CancellationToken cancellationToken)
    {
        if (targetWindow == IntPtr.Zero || NativeMethods.IsIconic(targetWindow))
            return null;

        if (_captureTarget != targetWindow)
        {
            _windowsGraphicsCapture?.Dispose();
            _windowsGraphicsCapture = null;
            _captureTarget = targetWindow;
            _wgcUnavailable = false;
            _wgcFailureReason = null;
        }

        if (!_wgcUnavailable)
        {
            try
            {
                _windowsGraphicsCapture ??=
                    new WindowsGraphicsCaptureBackend(targetWindow);

                return await _windowsGraphicsCapture.CaptureAsync(
                    targetWindow,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _windowsGraphicsCapture?.Dispose();
                _windowsGraphicsCapture = null;
                _wgcUnavailable = true;
                _wgcFailureReason = ex.Message;
            }
        }

        return CaptureWithGdi(targetWindow);
    }

    private static CaptureFrame? CaptureWithGdi(IntPtr targetWindow)
    {
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
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _windowsGraphicsCapture?.Dispose();
        _windowsGraphicsCapture = null;
    }
}
