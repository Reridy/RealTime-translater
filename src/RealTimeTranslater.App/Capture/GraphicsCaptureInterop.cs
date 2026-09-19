using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace RealTimeTranslater.App.Capture;

internal static class GraphicsCaptureInterop
{
    private static readonly Guid GraphicsCaptureItemGuid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(
            [In] IntPtr window,
            [In] ref Guid iid);

        IntPtr CreateForMonitor(
            [In] IntPtr monitor,
            [In] ref Guid iid);
    }

    internal static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemGuid;
        var pointer = interop.CreateForWindow(hwnd, ref iid);

        if (pointer == IntPtr.Zero)
            throw new InvalidOperationException(
                "Windows Graphics Capture could not create a capture item for this window.");

        try
        {
            return GraphicsCaptureItem.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }
}
