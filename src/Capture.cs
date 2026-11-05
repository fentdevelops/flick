using System;
using System.IO;

namespace Flick;

public enum TargetKind { Screen, Window, Area }

/// <summary>What to capture. Bounds are physical screen pixels at the time it was picked.</summary>
public sealed record CaptureTarget(TargetKind Kind, IntPtr Monitor, IntPtr Window, PxRect Bounds, PxRect MonitorBounds)
{
    /// <summary>Part of the captured surface to keep, relative to the surface's top-left. Null means all of it.</summary>
    public PxRect? Crop => Kind == TargetKind.Area ? new PxRect(Bounds.X - MonitorBounds.X, Bounds.Y - MonitorBounds.Y, Bounds.W, Bounds.H) : null;

    public static CaptureTarget ForMonitor(MonitorInfo m) => new(TargetKind.Screen, m.Handle, IntPtr.Zero, m.Bounds, m.Bounds);
    public static CaptureTarget ForWindow(IntPtr hwnd, PxRect bounds, MonitorInfo m) => new(TargetKind.Window, m.Handle, hwnd, bounds, m.Bounds);
    public static CaptureTarget ForArea(PxRect area, MonitorInfo m) => new(TargetKind.Area, m.Handle, IntPtr.Zero, area, m.Bounds);

    public bool StillValid() => Kind == TargetKind.Window ? Native.IsWindow(Window) : Native.MonitorAlive(Monitor);
}

/// <summary>One quality level. The same four levels apply to clips and screenshots.</summary>
public sealed record Quality(string Name, string Blurb, int ShortSide, int Fps, double BitsPerPixel, int PreviewJpeg, int ShotJpeg)
{
    public const int AudioBitsPerSecond = 160_000;

    public static readonly Quality[] All =
    {
        new("Tiny",     "Smallest file",     720, 30, 0.08, 30, 70),
        new("Good",     "Best for sharing", 1080, 30, 0.09, 55, 85),
        new("Sharp",    "Smooth and clear", 1440, 60, 0.06, 78, 93),
        new("Original", "Full detail",         0, 60, 0.10, 94, 0),   // screenshots: lossless PNG
    };

    public static Quality ByName(string? n) => Array.Find(All, q => q.Name == n) ?? All[1];

    /// <summary>Never upscales. Height and width stay even, as H.264 needs.</summary>
    public (int W, int H) OutputSize(int srcW, int srcH)
    {
        double scale = ShortSide <= 0 ? 1 : Math.Min(1, ShortSide / (double)Math.Min(srcW, srcH));
        return (Math.Max(16, (int)(srcW * scale) & ~1), Math.Max(16, (int)(srcH * scale) & ~1));
    }

    public int VideoBitrate(int srcW, int srcH)
    {
        var (w, h) = OutputSize(srcW, srcH);
        return (int)Math.Clamp(w * (double)h * Fps * BitsPerPixel, 600_000, 80_000_000);
    }

    /// <summary>Estimated file size of a clip, in bytes.</summary>
    public double EstimateBytes(int srcW, int srcH, double seconds, bool sound) =>
        (VideoBitrate(srcW, srcH) + (sound ? AudioBitsPerSecond : 0)) / 8.0 * seconds * 1.12; // container overhead, plus encoders run a little over target on busy video

    public string Detail(int srcW, int srcH)
    {
        var (w, h) = OutputSize(srcW, srcH);
        return $"{w}×{h} · {Fps} fps";
    }
}

public static class Fmt
{
    public static string Size(double bytes)
    {
        if (bytes < 1024 * 1024) return $"{Math.Max(1, bytes / 1024):0} KB";
        double mb = bytes / (1024 * 1024);
        if (mb < 10) return $"{mb:0.0} MB";
        if (mb < 1000) return $"{mb:0} MB";
        return $"{mb / 1024:0.0} GB";
    }
}

public static class Paths
{
    public static string Stamp(string prefix, string ext) => $"{prefix} {DateTime.Now:yyyy-MM-dd HH-mm-ss}{ext}";

    private static string Dir(Environment.SpecialFolder root)
    {
        string d = Path.Combine(Environment.GetFolderPath(root), "Flick");
        Directory.CreateDirectory(d);
        return d;
    }

    public static string Clips => Dir(Environment.SpecialFolder.MyVideos);
    public static string Shots => Dir(Environment.SpecialFolder.MyPictures);
    public static string AppData
    {
        get
        {
            string d = Environment.GetEnvironmentVariable("FLICK_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flick");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string Unique(string dir, string prefix, string ext)
    {
        string p = Path.Combine(dir, Stamp(prefix, ext));
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, Path.GetFileNameWithoutExtension(Stamp(prefix, ext)) + $" ({i}){ext}");
        return p;
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                string path = Path.Combine(Paths.AppData, "flick.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch { /* logging must never take the app down */ }
    }
}
