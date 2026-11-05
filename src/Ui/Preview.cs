using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flick;

/// <summary>Everything one quality card shows.</summary>
public sealed class CardModel : INotifyPropertyChanged
{
    public Quality Quality { get; }
    public string Name => Quality.Name;
    private BitmapSource? _image; private string _big = "", _small = "", _detail = ""; private bool _chosen;

    public BitmapSource? Image { get => _image; set => Set(ref _image, value); }
    public string Big { get => _big; set => Set(ref _big, value); }
    public string Small { get => _small; set => Set(ref _small, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public bool Chosen { get => _chosen; set => Set(ref _chosen, value); }

    public CardModel(Quality q) { Quality = q; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Builds the example pictures from the user's real screen: the busiest part of what they're about to capture,
/// shrunk and compressed the way each level would treat it, then shown at the same size so the difference is plain to see.
/// </summary>
public static class Preview
{
    public const int CardW = 153, CardH = 84;

    public sealed record Result(BitmapSource[] Images, long[] ShotBytes, int[] ShotW, int[] ShotH);

    public static Result Build(Grab g, double uiScale, bool measureShots = true)
    {
        int cw = Math.Min(g.W, (int)Math.Round(CardW * uiScale)), ch = Math.Min(g.H, (int)Math.Round(CardH * uiScale));
        var (cx, cy) = BusiestSpot(g, cw, ch);
        var crop = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++) Buffer.BlockCopy(g.Bgra, ((cy + y) * g.W + cx) * 4, crop, y * cw * 4, cw * 4);
        BitmapSource cropBmp = BitmapSource.Create(cw, ch, 96, 96, PixelFormats.Bgr32, null, crop, cw * 4);
        cropBmp.Freeze();

        var images = new BitmapSource[Quality.All.Length];
        var bytes = new long[images.Length];
        var ws = new int[images.Length]; var hs = new int[images.Length];
        for (int i = 0; i < images.Length; i++)
        {
            var q = Quality.All[i];
            var (ow, oh) = q.ShortSide <= 0 ? (g.W, g.H) : q.OutputSize(g.W, g.H);
            images[i] = Simulate(cropBmp, ow / (double)g.W, q.PreviewJpeg);

            // Encoding four full-screen pictures is the costly part, and only screenshot mode shows those sizes.
            if (measureShots) bytes[i] = Snap.Encode(g, q).Bytes.Length;
            ws[i] = ow; hs[i] = oh;
        }
        return new Result(images, bytes, ws, hs);
    }

    private static BitmapSource Simulate(BitmapSource crop, double scale, int jpeg)
    {
        BitmapSource small = crop;
        if (scale < 0.999)
        {
            var t = new TransformedBitmap(crop, new ScaleTransform(scale, scale));
            t.Freeze();
            small = t;
        }
        var enc = new JpegBitmapEncoder { QualityLevel = jpeg };
        enc.Frames.Add(BitmapFrame.Create(small));
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.StreamSource = ms; img.EndInit(); img.Freeze();
        return img;
    }

    /// <summary>Text and edges show quality best, so pick the part of the picture with the most of them.</summary>
    private static (int X, int Y) BusiestSpot(Grab g, int cw, int ch)
    {
        int bestX = Math.Max(0, (g.W - cw) / 2), bestY = Math.Max(0, (g.H - ch) / 2);
        if (g.W <= cw || g.H <= ch) return (bestX, bestY);
        long best = -1;
        int stepX = Math.Max(cw / 2, 8), stepY = Math.Max(ch / 2, 8);
        for (int y = 0; y + ch <= g.H; y += stepY)
            for (int x = 0; x + cw <= g.W; x += stepX)
            {
                long energy = 0;
                for (int sy = y; sy < y + ch; sy += 3)
                {
                    int row = sy * g.W;
                    for (int sx = x + 2; sx < x + cw; sx += 3)
                    {
                        int a = (row + sx) * 4, b = (row + sx - 2) * 4;
                        int d = g.Bgra[a + 1] - g.Bgra[b + 1];
                        energy += d < 0 ? -d : d;
                    }
                }
                if (energy > best) { best = energy; bestX = x; bestY = y; }
            }
        return (bestX, bestY);
    }
}
