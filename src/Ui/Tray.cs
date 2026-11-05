using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Flick;

/// <summary>The tray icon: click to show the bar, right-click for the menu.</summary>
internal sealed class Tray : IDisposable
{
    public const int Message = Native.WM_APP + 41;
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;

    [DllImport("user32")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr p);
    [DllImport("user32")] private static extern bool DestroyMenu(IntPtr menu);

    private readonly IntPtr _hwnd;
    private readonly BarWindow _bar;
    private readonly IntPtr _icon;
    private Native.NOTIFYICONDATA _data;

    public Tray(IntPtr hwnd, BarWindow bar)
    {
        _hwnd = hwnd; _bar = bar;
        _icon = Native.ExtractIconW(IntPtr.Zero, Environment.ProcessPath ?? "", 0);
        _data = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(), hWnd = hwnd, uID = 1, uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = Message, hIcon = _icon, szTip = "Flick: quick clips and screenshots", szInfo = "", szInfoTitle = "",
        };
        Native.Shell_NotifyIconW(NIM_ADD, ref _data);
    }

    public void Balloon(string title, string text)
    {
        var d = _data;
        d.uFlags = NIF_INFO; d.szInfoTitle = title; d.szInfo = text;
        Native.Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    public void OnMessage(int mouse)
    {
        if (mouse == Native.WM_LBUTTONUP) _bar.ToggleBar();
        else if (mouse == Native.WM_RBUTTONUP) ShowMenu();
    }

    private void ShowMenu()
    {
        const uint MF_STRING = 0, MF_CHECKED = 8, MF_SEPARATOR = 0x800, TPM_RETURNCMD = 0x100, TPM_BOTTOMALIGN = 0x20, TPM_RIGHTALIGN = 8;
        IntPtr m = CreatePopupMenu();
        AppendMenuW(m, MF_STRING, 1, "Show Flick\tCtrl+Shift+5");
        AppendMenuW(m, MF_STRING, 2, "How it works");
        AppendMenuW(m, MF_SEPARATOR, 0, null);
        AppendMenuW(m, MF_STRING, 3, "Open videos folder");
        AppendMenuW(m, MF_STRING, 4, "Open screenshots folder");
        AppendMenuW(m, MF_SEPARATOR, 0, null);
        AppendMenuW(m, MF_STRING | (StartWithWindows.Enabled ? MF_CHECKED : 0), 5, "Start with Windows");
        AppendMenuW(m, MF_SEPARATOR, 0, null);
        AppendMenuW(m, MF_STRING, 6, "Quit");
        Native.GetCursorPos(out var c);
        Native.SetForegroundWindow(_hwnd); // lets the menu close when clicking elsewhere
        int cmd = TrackPopupMenuEx(m, TPM_RETURNCMD | TPM_BOTTOMALIGN | TPM_RIGHTALIGN, c.X, c.Y, _hwnd, IntPtr.Zero);
        DestroyMenu(m);
        switch (cmd)
        {
            case 1: _bar.ShowBar(); break;
            case 2: _bar.ShowBar(); _bar.ShowGuide(); break;
            case 3: Open(Paths.Clips); break;
            case 4: Open(Paths.Shots); break;
            case 5: StartWithWindows.Enabled = !StartWithWindows.Enabled; break;
            case 6: _bar.QuitApp(); break;
        }
    }

    private static void Open(string folder)
    {
        try { Process.Start("explorer.exe", $"\"{folder}\""); }
        catch (Exception e) { Log.Write("open folder failed: " + e.Message); }
    }

    public void Dispose()
    {
        Native.Shell_NotifyIconW(NIM_DELETE, ref _data);
        if (_icon != IntPtr.Zero) Native.DestroyIcon(_icon);
    }
}
