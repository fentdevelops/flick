using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flick;

/// <summary>
/// `Flick.exe --flow-test &lt;folder&gt;`: drives the real bar and stop pill on screen, without taking focus or touching the mouse.
/// Proves the "never in the recording" promise with a control: captures the window's spot with hiding on, then with hiding off,
/// and they must differ. Restores the clipboard afterward. Set FLICK_DATA to keep test settings apart from real ones.
/// </summary>
internal static class FlowTest
{
    private static StreamWriter _o = null!;
    private static int _fail;

    private static void Check(bool ok, string what)
    {
        _o.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
        if (!ok) _fail++;
    }

    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        _o = new StreamWriter(Path.Combine(dir, "flow.txt")) { AutoFlush = true };

        // Whatever the user had copied goes back at the end.
        string? savedText = null; BitmapSource? savedImage = null; StringCollection? savedFiles = null;
        try
        {
            if (Clipboard.ContainsText()) savedText = Clipboard.GetText();
            if (Clipboard.ContainsImage()) savedImage = Clipboard.GetImage();
            if (Clipboard.ContainsFileDropList()) savedFiles = Clipboard.GetFileDropList();
        }
        catch { }

        // A plain test window to record, kept under every other window.
        var test = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Width = 800, Height = 500, Left = 0, Top = 0, Background = Brushes.SeaGreen, Title = "Flick flow test", Content = new TextBlock { Text = "Flow test", FontSize = 60, Foreground = Brushes.White, Margin = new Thickness(40) } };
        var th = new WindowInteropHelper(test).EnsureHandle();
        test.Show();
        Native.SetWindowPos(th, new IntPtr(1), 0, 0, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);
        await Task.Delay(300);
        var mon = Native.Monitors().First(m => m.Primary);
        var target = CaptureTarget.ForWindow(th, Native.VisibleWindowBounds(th)!.Value, mon);

        CheckAreas();

        var bar = new BarWindow();
        try
        {
            bar.TestSetTarget(target);
            bar.ShowBar();
            await Task.Delay(1400);
            Check(bar.IsVisible, "bar shows");

            // The real buttons, through their real click handlers.
            bar.TestClick("ModeShot");
            Check(bar.TestChecked("ModeShot") && !bar.TestChecked("ModeRecord") && bar.TestGoText == "Capture", "Screenshot button switches mode");
            Check(!((System.Windows.UIElement)bar.FindName("SoundButton")).IsEnabled, "Sound is unavailable for screenshots");
            bar.TestClick("ModeRecord");
            Check(bar.TestChecked("ModeRecord") && bar.TestGoText == "Record", "Record button switches mode");
            bool soundBefore = bar.TestChecked("SoundButton");
            bar.TestClick("SoundButton");
            Check(bar.TestChecked("SoundButton") != soundBefore && bar.TestWord("SoundButton") == (soundBefore ? "Sound off" : "Sound on"), "Sound button turns sound off");
            bar.TestClick("SoundButton");
            Check(bar.TestChecked("SoundButton") == soundBefore, "Sound button turns sound back on");
            bool stripBefore = bar.TestStripVisible;
            bar.TestClick("QualityButton");
            await Task.Delay(600);
            Check(bar.TestStripVisible != stripBefore, "Quality button closes the strip");
            bar.TestClick("QualityButton");
            await Task.Delay(1500);
            Check(bar.TestStripVisible, "Quality button opens the strip");
            Check(bar.TestCards.All(c => c.Image != null), "every quality card has an example picture");
            Check(bar.TestCards.Count(c => c.Chosen) == 1, "exactly one quality chosen");

            // Choosing a quality closes the strip; only the Quality button opens it again.
            bar.TestPickCard("Tiny");
            await Task.Delay(700);
            Check(!bar.TestStripVisible, "choosing a quality closes the strip");
            Check(bar.TestWord("QualityButton") == "Tiny" && bar.TestCards.Single(c => c.Chosen).Name == "Tiny", "the choice took effect");
            bar.TestClick("QualityButton");
            await Task.Delay(1200);
            Check(bar.TestStripVisible, "Quality button opens the strip again");
            bar.TestPickCard("Good");
            await Task.Delay(700);

            await CheckOutline(bar, dir);
            bar.TestSetTarget(target);

            // Hotkey message: hides, shows.
            Native.PostMessage(bar.TestHandle, Native.WM_HOTKEY, new IntPtr(0xF11C), IntPtr.Zero);
            await Task.Delay(700);
            Check(!bar.TestShown, "hotkey hides the bar");
            Native.PostMessage(bar.TestHandle, Native.WM_HOTKEY, new IntPtr(0xF11C), IntPtr.Zero);
            await Task.Delay(1500);
            Check(bar.TestShown && bar.IsVisible, "hotkey brings the bar back");

            // How the bar truly renders on screen: capture only its own window, with hiding off for a moment.
            Native.SetWindowDisplayAffinity(bar.TestHandle, Native.WDA_NONE);
            await Task.Delay(200);
            Native.GetWindowRect(bar.TestHandle, out var wr);
            var barPic = Snap.Capture(CaptureTarget.ForWindow(bar.TestHandle, new PxRect(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top), mon));
            Native.SetWindowDisplayAffinity(bar.TestHandle, Native.WDA_EXCLUDEFROMCAPTURE);
            var picEnc = new PngBitmapEncoder();
            picEnc.Frames.Add(BitmapFrame.Create(barPic.ToBitmap()));
            using (var pf = File.Create(Path.Combine(dir, "real-bar.png"))) picEnc.Save(pf);
            long ex = Native.GetWindowLongPtr(bar.TestHandle, Native.GWL_EXSTYLE);
            Check((ex & Native.WS_EX_NOACTIVATE) != 0, "bar can't steal focus");
            Check((ex & Native.WS_EX_TOPMOST) != 0, "bar is above all apps");

            // People use this PC while the test runs. If the bar was closed by hand, say so rather than blaming the capture.
            Native.GetCursorPos(out var mouse);
            _o.WriteLine($"      before capture check: bar shown {bar.TestShown}, visible {bar.IsVisible}, mouse at {mouse.X},{mouse.Y}");
            Check(bar.TestShown, "bar still open for the capture check (nobody closed it during the test)");
            Native.GetWindowRect(bar.TestHandle, out var r);
            double s = Win.Scale(bar.TestHandle);
            var barSpot = new PxRect(r.Left + (r.Right - r.Left) / 2 - (int)(30 * s), r.Bottom - (int)(84 * s), (int)(60 * s), (int)(50 * s));
            await ProveHidden(bar.TestHandle, barSpot, "bar", Path.Combine(dir, "bar-control"));

            // Screenshot → clipboard.
            await bar.TestGo(false, target);
            await Task.Delay(400);
            Check(Clipboard.ContainsImage(), "screenshot on clipboard as a picture");
            Check(Clipboard.ContainsFileDropList(), "screenshot on clipboard as a file");
            string shotFile = Clipboard.GetFileDropList()[0]!;
            Check(File.Exists(shotFile), "screenshot file saved");
            _o.WriteLine("      toast: " + bar.TestToastText);
            File.Delete(shotFile);

            // Record → bar disappears, pill appears → stop → bar returns, clipboard has the file.
            await bar.TestGo(true, target);
            await Task.Delay(1500);
            Check(bar.TestState == "Recording", "recording started");
            Check(!bar.TestShown && !bar.IsVisible, "bar disappeared while recording");
            Check(bar.TestPill.IsVisible, "stop pill visible while recording");
            long pex = Native.GetWindowLongPtr(bar.TestPill.Handle, Native.GWL_EXSTYLE);
            Check((pex & Native.WS_EX_NOACTIVATE) != 0, "stop pill can't steal focus");

            Native.GetWindowRect(bar.TestPill.Handle, out var pr);
            double ps = Win.Scale(bar.TestPill.Handle);
            var pillSpot = new PxRect(pr.Left + (pr.Right - pr.Left) / 2 - (int)(40 * ps), pr.Top + (int)(20 * ps), (int)(80 * ps), (int)(32 * ps));
            await ProveHidden(bar.TestPill.Handle, pillSpot, "stop pill", Path.Combine(dir, "pill-control"));

            Native.PostMessage(bar.TestHandle, Native.WM_HOTKEY, new IntPtr(0xF11C), IntPtr.Zero); // stop by hotkey
            for (int i = 0; i < 100 && bar.TestState != "Idle"; i++) await Task.Delay(50);
            await Task.Delay(1500);
            Check(bar.TestState == "Idle", "recording finished");
            Check(bar.IsVisible && bar.TestShown, "bar came back");
            Check(!bar.TestPill.IsVisible, "stop pill went away");
            Check(Clipboard.ContainsFileDropList(), "video on clipboard as a file");
            string clip = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList()[0]! : "";
            Check(clip.EndsWith(".mp4") && File.Exists(clip) && new FileInfo(clip).Length > 1000, "video file saved");
            Check(Clipboard.GetData("Preferred DropEffect") != null, "marked as copy, so pasting doesn't move it");
            _o.WriteLine("      toast: " + bar.TestToastText);
            if (File.Exists(clip)) File.Delete(clip);
        }
        catch (Exception e) { Check(false, "flow threw: " + e); }
        finally
        {
            try
            {
                var back = new DataObject();
                bool any = false;
                if (savedText != null) { back.SetText(savedText); any = true; }
                if (savedImage != null) { back.SetImage(savedImage); any = true; }
                if (savedFiles != null) { back.SetFileDropList(savedFiles); any = true; }
                if (any) Clipboard.SetDataObject(back, true); else Clipboard.Clear();
            }
            catch (Exception e) { _o.WriteLine("clipboard restore failed: " + e.Message); }
            bar.CloseDown();
            test.Close();
        }

        _o.WriteLine(_fail == 0 ? "ALL PASSED" : $"{_fail} FAILED");
        _o.Flush();
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>The dotted line and its control, on every screen, including recording.</summary>
    private static async Task CheckOutline(BarWindow bar, string dir)
    {
        foreach (var m in Native.Monitors())
        {
            var start = new PxRect(m.Bounds.X + m.Bounds.W / 4, m.Bounds.Y + m.Bounds.H / 4, 700, 400);
            bar.TestSetTarget(CaptureTarget.ForArea(start, m));
            await Task.Delay(500);
            var o = bar.TestOutline;
            Check(o.IsVisible && o.TestDotted, $"screen {m.Index}: dotted rounded line shows");
            Check(o.TestControlVisible && !o.TestClickThrough, $"screen {m.Index}: move and resize control is usable");
            Native.GetWindowDisplayAffinity(o.TestHandle, out uint aff);
            Check(aff == Native.WDA_EXCLUDEFROMCAPTURE, $"screen {m.Index}: outline is hidden from captures");
            Native.GetWindowRect(o.TestHandle, out var r);
            Check(r.Left <= start.X && r.Top <= start.Y && r.Right >= start.Right && r.Bottom >= start.Bottom, $"screen {m.Index}: window surrounds the area");

            // The real render, from the outline's own window.
            Native.SetWindowDisplayAffinity(o.TestHandle, Native.WDA_NONE);
            await Task.Delay(200);
            var pic = Snap.Capture(CaptureTarget.ForWindow(o.TestHandle, new PxRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), m));
            Native.SetWindowDisplayAffinity(o.TestHandle, Native.WDA_EXCLUDEFROMCAPTURE);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(pic.ToBitmap()));
            using (var f = File.Create(Path.Combine(dir, $"outline-screen{m.Index}.png"))) enc.Save(f);

            o.TestDrag(true, 50, 30);
            Check(o.TestArea == new PxRect(start.X + 50, start.Y + 30, 700, 400), $"screen {m.Index}: moves");
            Check(bar.TestTarget?.Bounds == o.TestArea, $"screen {m.Index}: a move changes what gets captured");
            o.TestDrag(false, 60, -40);
            var resized = new PxRect(start.X + 50, start.Y - 10, 760, 440);
            Check(o.TestArea == resized, $"screen {m.Index}: resizes from the top right ({o.TestArea.W}x{o.TestArea.H})");
            Check(bar.TestTarget?.Bounds == resized, $"screen {m.Index}: a resize changes what gets captured");
            Check(OutlineWindow.MovedArea(resized, m.Bounds, 100000, 100000).Right == m.Bounds.Right, $"screen {m.Index}: moving stops at the screen edge");
            var small = OutlineWindow.ResizedArea(resized, m.Bounds, -100000, 100000);
            Check(small.W == OutlineWindow.MinSize && small.H == OutlineWindow.MinSize && small.X == resized.X && small.Bottom == resized.Bottom, $"screen {m.Index}: resizing stops at the smallest size");
        }

        // The X removes the area, and nothing is left to capture until a new one is chosen.
        var removable = Native.Monitors().First(m => m.Primary);
        bar.TestSetTarget(CaptureTarget.ForArea(new PxRect(removable.Bounds.X + 300, removable.Bounds.Y + 300, 500, 300), removable));
        await Task.Delay(300);
        Check(bar.TestOutline.IsVisible, "area shows before removing it");
        bar.TestOutline.TestRemove();
        await Task.Delay(200);
        Check(!bar.TestOutline.IsVisible, "X hides the area");
        Check(bar.TestTarget == null, "X forgets the area");
        Check(bar.TestChecked("TargetArea"), "Area stays selected, ready for a new one");

        // While recording: the line stays, the control goes, clicks pass through, and moving is off.
        var mon = Native.Monitors().First(m => m.Primary);
        var area = CaptureTarget.ForArea(new PxRect(mon.Bounds.X + 200, mon.Bounds.Y + 200, 640, 360), mon);
        await bar.TestGo(true, area);
        await Task.Delay(900);
        var line = bar.TestOutline;
        Check(line.IsVisible && line.TestClickThrough && !line.TestControlVisible, "while recording the line stays, with no control, and clicks pass through");
        bar.StopRecording();
        for (int i = 0; i < 100 && bar.TestState != "Idle"; i++) await Task.Delay(50);
        await Task.Delay(800);
        Check(line.IsVisible && line.TestControlVisible && !line.TestClickThrough, "after recording the control comes back");
        string clip = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList()[0]! : "";
        if (File.Exists(clip)) File.Delete(clip);
    }

    /// <summary>
    /// On every monitor, including ones with negative coordinates: an area drag gives the right rectangle, and capturing it
    /// returns the pixels at that spot. Compares numbers only; no picture of the screen is kept.
    /// </summary>
    private static void CheckAreas()
    {
        var picker = typeof(Picker);
        foreach (var m in Native.Monitors())
        {
            var surf = new PickerSurface(null!, TargetKind.Area, m, 1, null);
            new WindowInteropHelper(surf).EnsureHandle();
            int x1 = m.Bounds.X + m.Bounds.W / 3, y1 = m.Bounds.Y + m.Bounds.H / 3, x2 = x1 + 501, y2 = y1 + 301;
            var (area, local, ok) = surf.TestDrag(x2, y2, x1, y1); // dragged up and to the left
            Check(ok && area == new PxRect(x1, y1, 501, 301), $"screen {m.Index}: drag gives {area}");
            Check(Math.Abs(local.X - (x1 - m.Bounds.X) / Win.Scale(new WindowInteropHelper(surf).Handle)) < 1, $"screen {m.Index}: drawn in the right place");
            var (tiny, _, tinyOk) = surf.TestDrag(x1, y1, x1 + 3, y1 + 3);
            Check(!tinyOk, "a stray click doesn't count as an area");
            var (past, _, _) = surf.TestDrag(m.Bounds.Right - 10, m.Bounds.Bottom - 10, m.Bounds.Right + 500, m.Bounds.Bottom + 500);
            Check(past.Right <= m.Bounds.Right && past.Bottom <= m.Bounds.Bottom, "dragging past the edge stays on that screen");
            surf.Close();

            var r = new PxRect(x1, y1, 500, 300);
            var grab = Snap.Capture(CaptureTarget.ForArea(r, m));
            var gdi = Native.CopyScreen(r);
            int same = 0, total = Math.Min(grab.Bgra.Length, gdi.Length) / 4;
            for (int i = 0; i < total; i++) if (Math.Abs(grab.Bgra[i * 4 + 1] - gdi[i * 4 + 1]) <= 8) same++;
            Check(grab.W == 500 && grab.H == 300, $"screen {m.Index}: area captures at {grab.W}x{grab.H}");
            Check(same > total * 0.7, $"screen {m.Index}: captured the right spot ({100 * same / Math.Max(1, total)}% match)");
        }
    }

    /// <summary>The same capture path recordings use, with hiding on and then off. If they differ, hiding really is what keeps it out.</summary>
    private static async Task ProveHidden(IntPtr hwnd, PxRect spot, string name, string file)
    {
        Native.GetWindowDisplayAffinity(hwnd, out uint aff);
        Check(aff == Native.WDA_EXCLUDEFROMCAPTURE, $"{name} set to hide from captures (0x{aff:X})");

        var mon = Native.Monitors().First(m => m.Bounds.Contains(spot.X, spot.Y));
        var t = CaptureTarget.ForArea(spot, mon);
        var hidden = Snap.Capture(t);
        Native.SetWindowDisplayAffinity(hwnd, Native.WDA_NONE);
        await Task.Delay(250);
        var visible = Snap.Capture(t);
        Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        await Task.Delay(120);

        int diff = 0, total = Math.Min(hidden.Bgra.Length, visible.Bgra.Length) / 4;
        for (int i = 0; i < total; i++)
            if (Math.Abs(hidden.Bgra[i * 4 + 1] - visible.Bgra[i * 4 + 1]) > 6) diff++;
        double pct = 100.0 * diff / Math.Max(1, total);
        Check(pct > 30, $"{name} is missing from a real capture ({pct:0}% of the spot changes when hiding is switched off)");

        // Only the control picture is kept, which shows Flick's own window rather than the user's screen.
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(visible.ToBitmap()));
        using var f = File.Create(file + ".png");
        enc.Save(f);
    }
}
