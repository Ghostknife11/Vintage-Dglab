using System.Runtime.InteropServices;
using System.Text;

namespace VintageDLab.Util;

/// <summary>
/// Sets clipboard text via Win32 P/Invoke. No GUI, no external deps.
/// </summary>
public static class ClipboardHelper
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    public static bool SetText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            if (!EmptyClipboard()) return false;

            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
            if (hMem == IntPtr.Zero) return false;

            var ptr = GlobalLock(hMem);
            if (ptr == IntPtr.Zero) { GlobalAlloc(0, UIntPtr.Zero); return false; } // free

            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            GlobalUnlock(hMem);

            SetClipboardData(CF_UNICODETEXT, hMem);
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }
}
