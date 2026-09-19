using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RealTimeTranslater.App.Interop;
using RealTimeTranslater.Core.Models;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace RealTimeTranslater.App.Capture;

internal sealed class WindowsGraphicsCaptureBackend : IDisposable
{
    private readonly IDirect3DDevice _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;

    private SizeInt32 _frameSize;
    private bool _disposed;

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }

    internal WindowsGraphicsCaptureBackend(IntPtr targetWindow)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new PlatformNotSupportedException(
                "Windows Graphics Capture is not supported on this system.");

        _device = Direct3D11Interop.CreateDevice();
        _item = GraphicsCaptureInterop.CreateItemForWindow(targetWindow);
        _frameSize = _item.Size;

        if (_frameSize.Width < 2 || _frameSize.Height < 2)
            throw new InvalidOperationException("The selected window has no capturable area.");

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _frameSize);

        _session = _framePool.CreateCaptureSession(_item);
        _session.StartCapture();
    }

    internal async Task<CaptureFrame?> CaptureAsync(
        IntPtr targetWindow,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (targetWindow == IntPtr.Zero || NativeMethods.IsIconic(targetWindow))
            return null;

        using var frame = _framePool.TryGetNextFrame();
        if (frame is null)
            return null;

        var contentSize = frame.ContentSize;
        var shouldResize =
            contentSize.Width > 1 &&
            contentSize.Height > 1 &&
            (contentSize.Width != _frameSize.Width ||
             contentSize.Height != _frameSize.Height);

        using var softwareBitmap =
            await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface)
                .AsTask(cancellationToken);

        var fullBitmap = CopyToDrawingBitmap(softwareBitmap);

        if (shouldResize)
        {
            _frameSize = contentSize;
            _framePool.Recreate(
                _device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _frameSize);
        }

        return CreateClientFrame(targetWindow, fullBitmap);
    }

    private static CaptureFrame? CreateClientFrame(
        IntPtr targetWindow,
        Bitmap fullBitmap)
    {
        if (!NativeMethods.GetClientRect(targetWindow, out var clientRect) ||
            !NativeMethods.GetWindowRect(targetWindow, out var windowRect))
        {
            fullBitmap.Dispose();
            return null;
        }

        var clientWidth = clientRect.Right - clientRect.Left;
        var clientHeight = clientRect.Bottom - clientRect.Top;
        var windowWidth = windowRect.Right - windowRect.Left;
        var windowHeight = windowRect.Bottom - windowRect.Top;

        if (clientWidth < 2 ||
            clientHeight < 2 ||
            windowWidth < 2 ||
            windowHeight < 2)
        {
            fullBitmap.Dispose();
            return null;
        }

        var clientOrigin = new NativeMethods.Point { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(targetWindow, ref clientOrigin))
        {
            fullBitmap.Dispose();
            return null;
        }

        var scaleX = fullBitmap.Width / (double)windowWidth;
        var scaleY = fullBitmap.Height / (double)windowHeight;

        var cropX = (int)Math.Round(
            (clientOrigin.X - windowRect.Left) * scaleX);
        var cropY = (int)Math.Round(
            (clientOrigin.Y - windowRect.Top) * scaleY);
        var cropWidth = (int)Math.Round(clientWidth * scaleX);
        var cropHeight = (int)Math.Round(clientHeight * scaleY);

        cropX = Math.Clamp(cropX, 0, Math.Max(0, fullBitmap.Width - 1));
        cropY = Math.Clamp(cropY, 0, Math.Max(0, fullBitmap.Height - 1));
        cropWidth = Math.Clamp(
            cropWidth,
            1,
            Math.Max(1, fullBitmap.Width - cropX));
        cropHeight = Math.Clamp(
            cropHeight,
            1,
            Math.Max(1, fullBitmap.Height - cropY));

        Bitmap clientBitmap;
        try
        {
            clientBitmap = fullBitmap.Clone(
                new Rectangle(cropX, cropY, cropWidth, cropHeight),
                PixelFormat.Format32bppArgb);
        }
        finally
        {
            fullBitmap.Dispose();
        }

        var dpi = NativeMethods.GetDpiForWindow(targetWindow);
        var dpiScale = dpi == 0 ? 1.0 : dpi / 96.0;

        return new CaptureFrame(
            clientBitmap,
            new PixelRect(
                clientOrigin.X,
                clientOrigin.Y,
                clientWidth,
                clientHeight),
            dpiScale);
    }

    private static unsafe Bitmap CopyToDrawingBitmap(
        SoftwareBitmap softwareBitmap)
    {
        SoftwareBitmap? converted = null;
        var source = softwareBitmap;

        if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
            softwareBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
        {
            converted = SoftwareBitmap.Convert(
                softwareBitmap,
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);
            source = converted;
        }

        try
        {
            using var buffer = source.LockBuffer(BitmapBufferAccessMode.Read);
            using var reference = buffer.CreateReference();

            reference
                .As<IMemoryBufferByteAccess>()
                .GetBuffer(out var sourceBytes, out _);

            var plane = buffer.GetPlaneDescription(0);
            var bitmap = new Bitmap(
                source.PixelWidth,
                source.PixelHeight,
                PixelFormat.Format32bppArgb);

            var targetData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                var rowBytes = checked(bitmap.Width * 4);
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var sourceRow =
                        (IntPtr)(sourceBytes + plane.StartIndex + y * plane.Stride);
                    var targetRow = IntPtr.Add(
                        targetData.Scan0,
                        y * targetData.Stride);

                    Buffer.MemoryCopy(
                        sourceRow.ToPointer(),
                        targetRow.ToPointer(),
                        Math.Abs(targetData.Stride),
                        rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(targetData);
            }

            return bitmap;
        }
        finally
        {
            converted?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _session.Dispose();
        _framePool.Dispose();
        _item.Dispose();
        _device.Dispose();
    }
}
