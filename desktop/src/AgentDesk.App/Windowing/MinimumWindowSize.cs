using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace AgentDesk.App;

internal sealed class MinimumWindowSize : IDisposable
{
    private const uint WmGetMinMaxInfo = 0x0024;
    private const nuint SubclassId = 1;

    private readonly nint _windowHandle;
    private readonly int _minimumWidth;
    private readonly int _minimumHeight;
    private readonly SubclassProcedure _subclassProcedure;
    private bool _disposed;

    public MinimumWindowSize(
        Window window,
        int minimumWidth,
        int minimumHeight,
        int initialWidth,
        int initialHeight)
    {
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _minimumWidth = minimumWidth;
        _minimumHeight = minimumHeight;
        _subclassProcedure = WindowSubclass;

        if (!SetWindowSubclass(
                _windowHandle,
                _subclassProcedure,
                SubclassId,
                0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var dpi = GetDpiForWindow(_windowHandle);
        var workArea = DisplayArea.GetFromWindowId(
            window.AppWindow.Id,
            DisplayAreaFallback.Primary).WorkArea;
        var margin = ScaleForDpi(16, dpi);
        var targetWidth = Math.Min(
            ScaleForDpi(initialWidth, dpi),
            workArea.Width - (margin * 2));
        var targetHeight = Math.Min(
            ScaleForDpi(initialHeight, dpi),
            workArea.Height - (margin * 2));
        targetWidth = Math.Max(targetWidth, ScaleForDpi(minimumWidth, dpi));
        targetHeight = Math.Max(targetHeight, ScaleForDpi(minimumHeight, dpi));

        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            workArea.X + ((workArea.Width - targetWidth) / 2),
            workArea.Y + ((workArea.Height - targetHeight) / 2),
            targetWidth,
            targetHeight));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        RemoveWindowSubclass(_windowHandle, _subclassProcedure, SubclassId);
        _disposed = true;
    }

    private nint WindowSubclass(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        if (message == WmGetMinMaxInfo)
        {
            var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var dpi = GetDpiForWindow(windowHandle);
            minMaxInfo.MinimumTrackSize.X = ScaleForDpi(_minimumWidth, dpi);
            minMaxInfo.MinimumTrackSize.Y = ScaleForDpi(_minimumHeight, dpi);
            Marshal.StructureToPtr(minMaxInfo, lParam, fDeleteOld: false);
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private static int ScaleForDpi(int value, uint dpi)
    {
        return (int)Math.Ceiling(value * Math.Max(dpi, 96u) / 96d);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaximumSize;
        public Point MaximumPosition;
        public Point MinimumTrackSize;
        public Point MaximumTrackSize;
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint windowHandle,
        SubclassProcedure subclassProcedure,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint windowHandle,
        SubclassProcedure subclassProcedure,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);
}
