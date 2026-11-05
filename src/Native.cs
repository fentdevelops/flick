using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Flick;

/// <summary>A rectangle in physical screen pixels. Everything capture-related uses these, never WPF units.</summary>
public readonly record struct PxRect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;
}

public sealed record MonitorInfo(IntPtr Handle, PxRect Bounds, bool Primary, int Index);

internal static unsafe class Native
{
    public const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;
    public const int WM_HOTKEY = 0x312, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, WM_DISPLAYCHANGE = 0x7E;
    public const int WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_APP = 0x8000;
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [DllImport("user32", SetLastError = true)] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32", SetLastError = true)] public static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] public static extern long GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] public static extern long SetWindowLongPtr(IntPtr h, int idx, long v);
    [DllImport("user32", SetLastError = true)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);

    private delegate bool MonitorEnumProc(IntPtr m, IntPtr hdc, RECT* r, IntPtr l);
    [DllImport("user32")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr l);
    [DllImport("user32", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("shcore")] public static extern int GetDpiForMonitor(IntPtr m, int type, out uint x, out uint y);

    [DllImport("dwmapi")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT v, int size);
    [DllImport("dwmapi", EntryPoint = "DwmGetWindowAttribute")] public static extern int DwmGetWindowAttributeInt(IntPtr h, int attr, out int v, int size);
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    [DllImport("gdi32")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("user32")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPels, biYPels, biClrUsed, biClrImportant;
    }

    /// <summary>Copies a part of the screen with GDI. Our own windows are excluded from capture, so they never show.</summary>
    public static byte[] CopyScreen(PxRect r)
    {
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = r.W, biHeight = -r.H, biPlanes = 1, biBitCount = 32 };
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        IntPtr dib = CreateDIBSection(screen, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) { DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); throw new InvalidOperationException("Screen copy failed."); }
        IntPtr old = SelectObject(mem, dib);
        BitBlt(mem, 0, 0, r.W, r.H, screen, r.X, r.Y, 0x00CC0020 /*SRCCOPY*/);
        var data = new byte[r.W * r.H * 4];
        Marshal.Copy(bits, data, 0, data.Length);
        for (int i = 3; i < data.Length; i += 4) data[i] = 255;
        SelectObject(mem, old); DeleteObject(dib); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        return data;
    }

    public static List<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, _, _, _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(m, ref mi))
                list.Add(new MonitorInfo(m, new PxRect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top), (mi.dwFlags & 1) != 0, list.Count + 1));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static PxRect WorkArea(IntPtr monitor)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref mi);
        return new PxRect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top);
    }

    public static bool MonitorAlive(IntPtr monitor)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(monitor, ref mi);
    }

    public static double MonitorScale(IntPtr monitor) => GetDpiForMonitor(monitor, 0, out uint x, out _) == 0 ? x / 96.0 : 1.0;

    public static IntPtr MonitorAt(int x, int y) => MonitorFromPoint(new POINT { X = x, Y = y }, 2 /*NEAREST*/);

    public static PxRect? VisibleWindowBounds(IntPtr h)
    {
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, sizeof(RECT)) != 0 && !GetWindowRect(h, out r)) return null;
        return new PxRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public sealed record WindowEntry(IntPtr Handle, PxRect Bounds, string Title);

    /// <summary>Real, visible app windows, front to back. Skips Flick's own windows.</summary>
    public static List<WindowEntry> PickableWindows(uint ownPid)
    {
        var list = new List<WindowEntry>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == ownPid) return true;
            if (DwmGetWindowAttributeInt(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0 || (ex & WS_EX_NOACTIVATE) != 0) return true;
            var cls = new StringBuilder(64); GetClassNameW(h, cls, 64);
            string c = cls.ToString();
            if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            var title = new StringBuilder(256); GetWindowTextW(h, title, 256);
            if (title.Length == 0) return true;
            var b = VisibleWindowBounds(h);
            if (b is not { W: > 40, H: > 40 }) return true;
            list.Add(new WindowEntry(h, b.Value, title.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // High-resolution pacing for the frame loop.
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr CreateWaitableTimerExW(IntPtr a, string? name, uint flags, uint access);
    [DllImport("kernel32", SetLastError = true)] public static extern bool SetWaitableTimer(IntPtr t, ref long due, int period, IntPtr cb, IntPtr arg, bool resume);
    [DllImport("kernel32")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32")] public static extern bool CloseHandle(IntPtr h);

    // Tray icon.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize; public IntPtr hWnd; public int uID; public int uFlags; public int uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    [DllImport("shell32", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIconW(int msg, ref NOTIFYICONDATA d);
    [DllImport("shell32", CharSet = CharSet.Unicode)] public static extern IntPtr ExtractIconW(IntPtr inst, string file, int idx);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr LoadImageW(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32")] public static extern bool DestroyIcon(IntPtr h);
}
