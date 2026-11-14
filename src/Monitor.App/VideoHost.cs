using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Monitor.App;
public sealed class VideoHost : HwndHost
{
    public nint VideoHandle
    {
        get;
        private set;
    }

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateWindowEx(int ex, string cls, string title, int style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32")]
    static extern bool DestroyWindow(nint handle);
    [DllImport("user32")]
    static extern bool InvalidateRect(nint handle, nint rectangle, bool erase);
    [DllImport("user32")]
    static extern bool UpdateWindow(nint handle);
    public void Clear()
    {
        Dispatcher.VerifyAccess();
        if (VideoHandle != 0)
        {
            InvalidateRect(VideoHandle, 0, true);
            UpdateWindow(VideoHandle);
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        VideoHandle = CreateWindowEx(0, "static", "", unchecked((int)0x56000004), 0, 0, 640, 360, parent.Handle, 0, 0, 0);
        if (VideoHandle == 0)
        {
            throw new System.ComponentModel.Win32Exception();
        }

        return new(this, VideoHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        VideoHandle = 0;
    }
}
