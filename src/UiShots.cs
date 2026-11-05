using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flick;

/// <summary>
/// `Flick.exe --ui-shots &lt;folder&gt;`: draws each state of the UI to a PNG with a transparent background, trimmed to the UI,
/// without showing anything on screen. The README's pictures come from here. All content is made up; nothing real is captured.
/// </summary>
internal static class UiShots
{
    private const int Scale = 2;

    public static async Task<int> Run(string dir)
    {
        Directory.CreateDirectory(dir);
        Anim.Instant = true;
        var bar = new BarWindow();
        var root = bar.TestRoot;
        bar.Content = null;
        var host = new Grid { Width = 848, Height = 560 };
        host.Children.Add(root);
        root.HorizontalAlignment = HorizontalAlignment.Center;
        root.VerticalAlignment = VerticalAlignment.Bottom;
        root.Width = 768; root.Height = 500;
        root.Opacity = 1;

        var g = FakeScreen();
        bar.TestSetMode(true);
        var pv = await Bg.Sta(() => Preview.Build(g, 1));
        bar.ApplyPreview(pv);
        var thumb = g.ToBitmap();

        async Task Shot(string name)
        {
            await Task.Delay(900); // lets the selection highlights finish fading in
            Save(host, host.Width, host.Height, Path.Combine(dir, name + ".png"));
        }

        bar.TestStripOpen(false);
        await Shot("bar");

        bar.TestStripOpen(true);
        await Shot("quality-video");

        bar.TestSetMode(false);
        bar.TestQuality("Sharp");
        await Shot("quality-screenshot");

        bar.TestStripOpen(false);
        bar.TestSetMode(true);
        bar.TestQuality("Good");
        bar.TestShowGuide(true);
        await Shot("first-run");
        bar.TestShowGuide(false);

        bar.TestToast("Copied. Press Ctrl+V to paste.", "0:12 video · 8.4 MB", thumb);
        await Shot("copied");
        bar.TestHideToast();

        // The stop pill.
        var pill = new PillWindow();
        var pillRoot = (Grid)pill.Content;
        pill.Content = null;
        var pillHost = new Grid { Width = 340, Height = 130 };
        pillHost.Children.Add(pillRoot);
        pillRoot.Opacity = 1;
        pillRoot.RenderTransform = Transform.Identity;
        pill.SoundIcon.Data = (Geometry)pill.FindResource("IconSoundOn");
        pill.Clock.Text = "0:12";
        pill.Hint.Opacity = 1;
        Save(pillHost, 340, 130, Path.Combine(dir, "stop-pill.png"));

        // The area line and its control.
        var outline = new OutlineWindow();
        var area = outline.DetachForPicture(520, 300);
        var areaHost = new Grid { Width = 560, Height = 380 };
        areaHost.Children.Add(area);
        Save(areaHost, 560, 380, Path.Combine(dir, "area.png"));

        return 0;
    }

    private static void Save(FrameworkElement e, double w, double h, string file)
    {
        e.Measure(new Size(w, h));
        e.Arrange(new Rect(0, 0, w, h));
        e.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)(w * Scale), (int)(h * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        rtb.Render(e);
        BitmapSource trimmed = Trim(rtb);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(trimmed));
        using var f = File.Create(file);
        enc.Save(f);
    }

    /// <summary>Crops away the empty transparent space around the UI.</summary>
    private static BitmapSource Trim(BitmapSource b)
    {
        int w = b.PixelWidth, h = b.PixelHeight;
        var px = new byte[w * h * 4];
        b.CopyPixels(px, w * 4, 0);
        int left = w, top = h, right = -1, bottom = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] != 0)
                {
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
        return right < 0 ? b : new CroppedBitmap(b, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
    }

    /// <summary>A made-up busy screen so the example pictures have something to show.</summary>
    private static Grab FakeScreen()
    {
        var panel = new Grid { Width = 2560, Height = 1440, Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF6)) };
        var text = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"), FontSize = 15, Margin = new Thickness(60), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Black,
            Text = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog while a small window shows charts, buttons and fine text. 0123456789. ", 200)),
        };
        panel.Children.Add(text);
        panel.Children.Add(new Border { Width = 400, Height = 240, Margin = new Thickness(90), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Background = new LinearGradientBrush(Colors.SteelBlue, Colors.OrangeRed, 30), CornerRadius = new CornerRadius(14) });
        panel.Measure(new Size(2560, 1440));
        panel.Arrange(new Rect(0, 0, 2560, 1440));
        var rtb = new RenderTargetBitmap(2560, 1440, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(panel);
        var data = new byte[2560 * 1440 * 4];
        rtb.CopyPixels(data, 2560 * 4, 0);
        return new Grab(data, 2560, 1440);
    }
}
