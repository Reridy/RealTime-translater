using System.Diagnostics;
using System.Text;
using RealTimeTranslater.App.Interop;

namespace RealTimeTranslater.App.Capture;

public static class WindowFinder
{
    public static IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        var windows = new List<WindowInfo>();
        var ownProcessId = (uint)Environment.ProcessId;

        NativeMethods.EnumWindows((handle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(handle) || NativeMethods.IsIconic(handle))
                return true;

            NativeMethods.GetWindowThreadProcessId(handle, out var processId);
            if (processId == ownProcessId)
                return true;

            var length = NativeMethods.GetWindowTextLength(handle);
            if (length <= 0)
                return true;

            var builder = new StringBuilder(length + 1);
            NativeMethods.GetWindowText(handle, builder, builder.Capacity);
            var title = builder.ToString().Trim();

            if (title.Length > 0)
                windows.Add(new WindowInfo(handle, title));

            return true;
        }, IntPtr.Zero);

        return windows
            .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
