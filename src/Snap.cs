using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Flick;

public sealed record Grab(byte[] Bgra, int W, int H)
{
    public BitmapSource ToBitmap()
    {
        var b = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, Bgra, W * 4);
        b.Freeze();
        return b;
    }
}

public sealed record EncodedShot(byte[] Bytes, string Extension, BitmapSource Picture);

public static class Snap
{
    /// <summary>Takes one frame of the target. Windows are captured even if something covers them. Falls back to a GDI copy.</summary>
    public static Grab Capture(CaptureTarget t, bool cursor = false)
    {
        try { return CaptureWgc(t, cursor); }
        catch (Exception e)
        {
            Log.Write("one-shot capture failed, using GDI: " + e.Message);
            var r = t.Bounds;
            return new Grab(Native.CopyScreen(r), r.W, r.H);
        }
    }

    private static Grab CaptureWgc(CaptureTarget t, bool cursor)
    {
        Grab? result = null; Exception? error = null;
        var th = new Thread(() =>
        {
            try { WgcSource.InitThread(); result = CaptureOn(t, cursor); }
            catch (Exception e) { error = e; }
        }) { IsBackground = true };
        th.SetApartmentState(ApartmentState.MTA);
        th.Start();
        th.Join();
        if (error != null) throw error;
        return result!;
    }

    private static Grab CaptureOn(CaptureTarget t, bool cursor)
    {
        var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device? dev, out ID3D11DeviceContext? ctx).CheckError();
        using (dev) using (ctx)
        using (new Finally(() => { ctx!.ClearState(); ctx.Flush(); }))
        using (var dxgi = dev!.QueryInterface<IDXGIDevice>())
        using (var src = new WgcSource(t, dxgi.NativePointer, cursor, 1))
        {
            src.Start();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1000)
            {
                if (src.TryGetFrame(out IntPtr f))
                {
                    try
                    {
                        var size = WgcSource.FrameSize(f);
                        using var tex = new ID3D11Texture2D(WgcSource.FrameTexture(f));
                        return ReadFrame(dev, ctx!, tex, t.Crop ?? new PxRect(0, 0, size.W, size.H), size);
                    }
                    finally { WgcSource.Close(f); }
                }
                Thread.Sleep(2);
            }
        }
        throw new TimeoutException("No frame arrived.");
    }

    private sealed class Finally(Action a) : IDisposable { public void Dispose() => a(); }

    /// <summary>
    /// For the example pictures, which refresh often. Screens and areas use a plain screen copy, so just having the bar open
    /// never wakes the GPU driver. Windows may be covered by others, so those need the real capture.
    /// </summary>
    public static Grab CaptureForPreview(CaptureTarget t)
    {
        if (t.Kind == TargetKind.Window) return Capture(t);
        return new Grab(Native.CopyScreen(t.Bounds), t.Bounds.W, t.Bounds.H);
    }

    private static unsafe Grab ReadFrame(ID3D11Device dev, ID3D11DeviceContext ctx, ID3D11Texture2D tex, PxRect crop, WgcSource.SizeInt32 size)
    {
        int x = Math.Clamp(crop.X, 0, size.W - 1), y = Math.Clamp(crop.Y, 0, size.H - 1);
        int w = Math.Min(crop.W, size.W - x), h = Math.Min(crop.H, size.H - y);
        using var staging = dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        ctx.CopySubresourceRegion(staging, 0, 0, 0, 0, tex, 0, new Vortice.Mathematics.Box(x, y, 0, x + w, y + h, 1));
        var map = ctx.Map(staging, 0, MapMode.Read);
        try
        {
            var data = new byte[w * h * 4];
            fixed (byte* d = data)
                for (int row = 0; row < h; row++)
                {
                    byte* s = (byte*)map.DataPointer + row * (int)map.RowPitch;
                    Buffer.MemoryCopy(s, d + row * w * 4, w * 4, w * 4);
                }
            for (int i = 3; i < data.Length; i += 4) data[i] = 255;
            return new Grab(data, w, h);
        }
        finally { ctx.Unmap(staging, 0); }
    }

    /// <summary>Encodes a screenshot at a quality level. The bytes are the real file, so their length is the true size.</summary>
    public static EncodedShot Encode(Grab g, Quality q)
    {
        BitmapSource bmp = BitmapSource.Create(g.W, g.H, 96, 96, PixelFormats.Bgr32, null, g.Bgra, g.W * 4);
        var (w, h) = q.ShortSide <= 0 ? (g.W, g.H) : q.OutputSize(g.W, g.H);
        if (w != g.W || h != g.H) bmp = new TransformedBitmap(bmp, new ScaleTransform(w / (double)g.W, h / (double)g.H));
        BitmapEncoder enc = q.ShotJpeg > 0 ? new JpegBitmapEncoder { QualityLevel = q.ShotJpeg } : new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        var bytes = ms.ToArray();
        // What gets pasted is what was saved: decode the encoded file back.
        var picture = q.ShotJpeg > 0 ? Decode(bytes) : Freeze(bmp);
        return new EncodedShot(bytes, q.ShotJpeg > 0 ? ".jpg" : ".png", picture);
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        var img = new BitmapImage();
        img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.StreamSource = new MemoryStream(bytes); img.EndInit(); img.Freeze();
        return img;
    }

    private static BitmapSource Freeze(BitmapSource b)
    {
        if (b.IsFrozen) return b;
        var w = new WriteableBitmap(b); w.Freeze(); return w;
    }

    /// <summary>Puts a screenshot on the clipboard as a picture and as a file, so it pastes anywhere.</summary>
    public static void CopyShot(BitmapSource picture, string file)
    {
        var data = new DataObject();
        data.SetImage(ToBgr(picture));
        AddFile(data, file);
        SetClipboard(data);
    }

    /// <summary>Video has no clipboard picture format, so it goes on as a file: pastes into chats, mail, Explorer.</summary>
    public static void CopyFile(string file)
    {
        var data = new DataObject();
        AddFile(data, file);
        SetClipboard(data);
    }

    private static BitmapSource ToBgr(BitmapSource s)
    {
        var c = new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);
        c.Freeze();
        return c;
    }

    private static void AddFile(DataObject d, string file)
    {
        d.SetFileDropList(new StringCollection { file });
        d.SetData("Preferred DropEffect", new MemoryStream(new byte[] { 5, 0, 0, 0 })); // copy
    }

    /// <summary>Another app may be holding the clipboard for a moment, so keep trying briefly. Must run on the UI thread.</summary>
    private static void SetClipboard(DataObject d)
    {
        Exception? last = null;
        for (int i = 0; i < 12; i++)
        {
            try { Clipboard.SetDataObject(d, true); return; }
            catch (Exception e) when (e is COMException or ExternalException) { last = e; Thread.Sleep(60); }
        }
        throw new InvalidOperationException("The clipboard is busy.", last);
    }
}
