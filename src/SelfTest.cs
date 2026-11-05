using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vortice.MediaFoundation;

namespace Flick;

/// <summary>
/// `Flick.exe --selftest &lt;folder&gt;`: records a moving test window placed off screen, so nothing appears on the desktop,
/// then reads each file back and checks it. Prints PASS or FAIL per check and exits non-zero on failure.
/// </summary>
internal static class SelfTest
{
    private static int _failures;
    private static TextWriter _out = Console.Out;

    private static void Check(bool ok, string what)
    {
        _out.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
        _out.Flush();
        if (!ok) _failures++;
    }

    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        _out = new StreamWriter(Path.Combine(dir, "selftest.txt")) { AutoFlush = true };

        var win = MakeTestWindow(out var tickText);
        ShowBehindEverything(win);
        var hwnd = new WindowInteropHelper(win).Handle;
        int n = 0;
        var anim = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        anim.Tick += (_, _) =>
        {
            n++;
            tickText.Text = n.ToString();
            Canvas.SetLeft(tickText, n * 3 % 900);
            ((SolidColorBrush)win.Background).Color = Color.FromRgb((byte)(n % 256), (byte)(n * 2 % 256), 60);
        };
        anim.Start();
        await Task.Delay(400);

        var mon = Native.Monitors()[0];
        var bounds = Native.VisibleWindowBounds(hwnd)!.Value;
        var target = CaptureTarget.ForWindow(hwnd, bounds, mon);
        _out.WriteLine($"test window {bounds.W}x{bounds.H} at {bounds.X},{bounds.Y}");

        // 1. One-shot screenshot of the window, at every quality.
        var grab = Snap.Capture(target);
        Check(grab.W == bounds.W && grab.H == bounds.H, $"screenshot size {grab.W}x{grab.H}");
        Check(NotBlank(grab), "screenshot has a real picture");
        foreach (var q in Quality.All)
        {
            var shot = Snap.Encode(grab, q);
            File.WriteAllBytes(Path.Combine(dir, $"shot-{q.Name}{shot.Extension}"), shot.Bytes);
            _out.WriteLine($"      shot {q.Name}: {shot.Picture.PixelWidth}x{shot.Picture.PixelHeight} {Fmt.Size(shot.Bytes.Length)}");
            Check(shot.Bytes.Length > 1000, $"shot {q.Name} encoded");
        }

        // 2. Clips at each level, with and without sound.
        const int seconds = 4;
        foreach (var (q, sound) in new[] { (Quality.All[0], false), (Quality.All[1], true), (Quality.All[2], true), (Quality.All[3], false) })
        {
            string path = Path.Combine(dir, $"clip-{q.Name}-{(sound ? "sound" : "mute")}.mp4");
            var rec = new Recorder(new RecordOptions(target, q, sound, path));
            rec.Start();
            await rec.Started;
            await Task.Delay(seconds * 1000);
            rec.Stop();
            var r = await rec.Completed;

            double est = q.EstimateBytes(bounds.W, bounds.H, r.Seconds, sound);
            _out.WriteLine($"      {q.Name}: {r.Width}x{r.Height} {r.Seconds:0.00}s {Fmt.Size(r.FileBytes)} (estimate up to {Fmt.Size(est)}), frames {r.Frames}, skipped {r.SkippedFrames}, hardware {r.HardwareEncoder}, sound {r.SoundCaptured}");
            Check(r.Frames >= (seconds - 0.5) * q.Fps, $"{q.Name}: wrote {r.Frames} frames");
            Check(r.SkippedFrames <= q.Fps * seconds / 20, $"{q.Name}: skipped {r.SkippedFrames} frames (5% allowed)");
            Check(r.SoundCaptured == sound, $"{q.Name}: sound {(sound ? "on" : "off")}");
            Check(Environment.GetEnvironmentVariable("FLICK_SOFTWARE") == "1" ? !r.HardwareEncoder : r.HardwareEncoder, $"{q.Name}: expected encoder in use (hardware {r.HardwareEncoder})");
            Verify(path, r, q, sound, dir);
        }

        // 3. Area crop on a real monitor: only checks the pipeline accepts a crop; the picture is not kept.
        anim.Stop();
        win.Close();
        var areaBounds = new PxRect(mon.Bounds.X + 100, mon.Bounds.Y + 100, 641, 361);
        string areaPath = Path.Combine(dir, "clip-area.mp4");
        var areaRec = new Recorder(new RecordOptions(CaptureTarget.ForArea(areaBounds, mon), Quality.All[3], false, areaPath));
        areaRec.Start();
        await areaRec.Started;
        await Task.Delay(1500);
        areaRec.Stop();
        var ar = await areaRec.Completed;
        Check(ar.Width == 640 && ar.Height == 360, $"area crop gives even output {ar.Width}x{ar.Height}");
        File.Delete(areaPath);

        // 4. Closing the window while recording ends the clip cleanly instead of losing it.
        var win2 = MakeTestWindow(out _);
        ShowBehindEverything(win2);
        await Task.Delay(300);
        var h2 = new WindowInteropHelper(win2).Handle;
        var t2 = CaptureTarget.ForWindow(h2, Native.VisibleWindowBounds(h2)!.Value, mon);
        string closePath = Path.Combine(dir, "clip-closed.mp4");
        var closeRec = new Recorder(new RecordOptions(t2, Quality.All[1], false, closePath));
        closeRec.Start();
        await closeRec.Started;
        await Task.Delay(1200);
        win2.Close();
        var cr = await closeRec.Completed;
        Check(cr.EndedEarlyBecause != null && cr.FileBytes > 0, $"closed window saved the clip ({cr.EndedEarlyBecause})");

        // 5. Repeated captures must not pile up GPU devices, threads or memory.
        {
            var mon0 = Native.Monitors()[0];
            var one = CaptureTarget.ForArea(new PxRect(mon0.Bounds.X + 10, mon0.Bounds.Y + 10, 200, 200), mon0);
            var me = System.Diagnostics.Process.GetCurrentProcess();
            for (int i = 0; i < 3; i++) Snap.Capture(one);
            await Task.Delay(1000);
            me.Refresh();
            long priv0 = me.PrivateMemorySize64; int th0 = me.Threads.Count;
            for (int i = 0; i < 15; i++) Snap.Capture(one);
            await Task.Delay(1500);
            GC.Collect();
            me.Refresh();
            long growth = (me.PrivateMemorySize64 - priv0) / 1048576; int thGrowth = me.Threads.Count - th0;
            _out.WriteLine($"      15 more captures: private memory {growth:+0;-0;0} MB, threads {thGrowth:+0;-0;0}");
            Check(growth < 40, $"captures don't pile up memory ({growth} MB)");
            Check(thGrowth < 8, $"captures don't pile up threads ({thGrowth})");
        }

        // 6. Whole real screens, for speed and size. Only numbers are kept; the files are deleted straight away.
        foreach (var m in Native.Monitors())
            foreach (var q in new[] { Quality.All[1], Quality.All[3] })
            {
                string p = Path.Combine(dir, "perf.mp4");
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                var rec = new Recorder(new RecordOptions(CaptureTarget.ForMonitor(m), q, true, p));
                rec.Start();
                await rec.Started;
                var cpu0 = proc.TotalProcessorTime;
                var t0 = DateTime.UtcNow;
                await Task.Delay(6000);
                proc.Refresh();
                double cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds / (DateTime.UtcNow - t0).TotalSeconds / Environment.ProcessorCount * 100;
                long mem = proc.WorkingSet64;
                rec.Stop();
                var r = await rec.Completed;
                double est = q.EstimateBytes(m.Bounds.W, m.Bounds.H, r.Seconds, true);
                _out.WriteLine($"      screen {m.Index} {q.Name}: {r.Width}x{r.Height} {r.Frames} frames, skipped {r.SkippedFrames}, hardware {r.HardwareEncoder}, {Fmt.Size(r.FileBytes)} vs estimate {Fmt.Size(est)}, CPU {cpu:0.0}%, memory {mem / 1048576} MB");
                Check(r.SkippedFrames <= r.Frames / 50, $"screen {m.Index} {q.Name}: smooth ({r.SkippedFrames} skipped)");
                File.Delete(p);
            }

        _out.WriteLine(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILED");
        _out.Flush();
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>On screen but under every other window, unfocused, so it renders without getting in anyone's way.</summary>
    private static void ShowBehindEverything(Window w)
    {
        var helper = new WindowInteropHelper(w);
        helper.EnsureHandle();
        var mon = Native.Monitors()[0];
        Native.SetWindowPos(helper.Handle, new IntPtr(1) /*HWND_BOTTOM*/, mon.Bounds.X, mon.Bounds.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        w.Show();
        Native.SetWindowPos(helper.Handle, new IntPtr(1), 0, 0, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);
    }

    private static Window MakeTestWindow(out TextBlock text)
    {
        text = new TextBlock { Text = "0", FontSize = 120, Foreground = Brushes.White, FontWeight = FontWeights.Bold };
        var canvas = new Canvas();
        canvas.Children.Add(text);
        Canvas.SetTop(text, 200);
        canvas.Children.Add(new TextBlock { Text = "Flick engine test. Fine text here shows quality differences.", FontSize = 22, Foreground = Brushes.White, Margin = new Thickness(40) });
        return new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            Left = 0, Top = 0, Width = 1280, Height = 720, Background = new SolidColorBrush(Colors.Black), Content = canvas, Title = "Flick test",
        };
    }

    private static bool NotBlank(Grab g)
    {
        var seen = new HashSet<int>();
        for (int i = 0; i < g.Bgra.Length; i += 4 * 97) seen.Add(g.Bgra[i] | g.Bgra[i + 1] << 8 | g.Bgra[i + 2] << 16);
        return seen.Count > 1;
    }

    /// <summary>Reads the file back with the same decoders Windows apps use.</summary>
    private static void Verify(string path, RecordResult r, Quality q, bool sound, string dir)
    {
        MediaFactory.MFStartup(true);
        try
        {
            using var attrs = MediaFactory.MFCreateAttributes(1);
            attrs.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
            using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, attrs);

            using (var want = MediaFactory.MFCreateMediaType())
            {
                want.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                want.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, want);
            }

            bool hasAudio = false;
            try { using var a = reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream); hasAudio = a != null; } catch { }
            Check(hasAudio == sound, $"{q.Name}: file {(hasAudio ? "has" : "has no")} sound track");

            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            reader.SetCurrentPosition(0);
            using var vt = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            vt.GetUInt64(MediaTypeAttributeKeys.FrameSize, out ulong fs);
            int w = (int)(fs >> 32), h = (int)(fs & 0xFFFFFFFF);
            Check(w == r.Width && h == r.Height, $"{q.Name}: decodes at {w}x{h}");

            int count = 0; long first = -1, last = 0; byte[]? mid = null; int stride = w * 4;
            HashSet<byte> values = new();
            while (true)
            {
                using var s = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out SourceReaderFlag flags, out long ts);
                if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
                if (s == null) continue; // "format changed" and similar notices carry no picture
                if (first < 0) first = ts;
                last = ts;
                count++;
                if (count == 60 || mid == null)
                {
                    using var b = s.ConvertToContiguousBuffer();
                    IntPtr p;
                    b.Lock(out p, out _, out int len);
                    mid = new byte[len];
                    Marshal.Copy(p, mid, 0, len);
                    b.Unlock();
                }
            }
            double seen = (last - first) / 1e7;
            Check(count >= r.Frames * 0.95, $"{q.Name}: decoded {count} of {r.Frames} frames");
            Check(Math.Abs(seen - r.Seconds) < 0.4, $"{q.Name}: plays for {seen:0.00}s (recorded {r.Seconds:0.00}s)");

            if (mid != null && mid.Length >= w * h * 4)
            {
                for (int i = 0; i < w * h * 4; i += 4 * 53) values.Add(mid[i]);
                Check(values.Count > 1, $"{q.Name}: frames have a real picture");
                for (int i = 3; i < w * h * 4; i += 4) mid[i] = 255;
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, mid, stride);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var fsOut = File.Create(Path.Combine(dir, $"frame-{Path.GetFileNameWithoutExtension(path)}.png"));
                enc.Save(fsOut);
            }
        }
        catch (Exception e) { Check(false, $"{q.Name}: read back failed: {e.Message}"); }
        finally { MediaFactory.MFShutdown(); }
    }
}
