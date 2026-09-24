using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace RealTimeTranslater.App.Capture;

internal static class Direct3D11Interop
{
    private const int D3dDriverTypeHardware = 1;
    private const uint D3d11CreateDeviceBgraSupport = 0x20;
    private const uint D3d11SdkVersion = 7;

    private static readonly Guid IidIdxgiDevice =
        new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out IntPtr device,
        out uint featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);

    internal static IDirect3DDevice CreateDevice()
    {
        var hr = D3D11CreateDevice(
            IntPtr.Zero,
            D3dDriverTypeHardware,
            IntPtr.Zero,
            D3d11CreateDeviceBgraSupport,
            IntPtr.Zero,
            0,
            D3d11SdkVersion,
            out var d3dDevice,
            out _,
            out var immediateContext);

        Marshal.ThrowExceptionForHR(hr);

        IntPtr dxgiDevice = IntPtr.Zero;
        IntPtr graphicsDevice = IntPtr.Zero;

        try
        {
            var iid = IidIdxgiDevice;
            hr = Marshal.QueryInterface(d3dDevice, ref iid, out dxgiDevice);
            Marshal.ThrowExceptionForHR(hr);

            hr = CreateDirect3D11DeviceFromDXGIDevice(
                dxgiDevice,
                out graphicsDevice);
            Marshal.ThrowExceptionForHR(hr);

            return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
        }
        finally
        {
            if (graphicsDevice != IntPtr.Zero)
                Marshal.Release(graphicsDevice);

            if (dxgiDevice != IntPtr.Zero)
                Marshal.Release(dxgiDevice);

            if (immediateContext != IntPtr.Zero)
                Marshal.Release(immediateContext);

            if (d3dDevice != IntPtr.Zero)
                Marshal.Release(d3dDevice);
        }
    }
}
