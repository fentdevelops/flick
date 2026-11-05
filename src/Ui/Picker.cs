using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Flick;

/// <summary>
/// Full-screen choosers for "which screen", "which window" and "what area". Each monitor gets an ordinary (not layered)
/// window showing a still of that screen, so dragging stays perfectly smooth even on big displays.
/// </summary>
internal sealed class Picker
{
    private readonly TaskCompletionSource<CaptureTarget?> _result = new();
    private readonly List<PickerSurface> _surfaces = new();
    private readonly TargetKind _kind;
    private readonly List<MonitorInfo> _monitors;
    private readonly List<Native.WindowEntry> _windows;
    private bool _finished;

    private Picker(TargetKind kind, List<MonitorInfo> monitors, List<Native.WindowEntry> windows)
    {
        _kind = kind; _monitors = monitors; _windows = windows;
    }

    public static Task<CaptureTarget?> Show(TargetKind kind, CaptureTarget? previous)
    {
        var monitors = Native.Monitors();
        var windows = kind == TargetKind.Window ? Native.PickableWindows((uint)Environment.ProcessId) : new();
        var p = new Picker(kind, monitors, windows);
        foreach (var m in monitors)
        {
            var s = new PickerSurface(p, kind, m, monitors.Count, previous?.Kind == kind && previous.Kind == TargetKind.Area && previous.Monitor == m.Handle ? previous.Bounds : null);
            p._surfaces.Add(s);
        }
        foreach (var s in p._surfaces) s.Show();
        // Take focus on the screen under the cursor so Esc works right away.
        Native.GetCursorPos(out var c);
        (p._surfaces.FirstOrDefault(s => s.Monitor.Bounds.Contains(c.X, c.Y)) ?? p._surfaces[0]).TakeFocus();
        p.UpdateHover(c.X, c.Y);
        return p._result.Task;
    }

    public void UpdateHover(int x, int y)
    {
        if (_kind == TargetKind.Window)
        {
            var w = _windows.FirstOrDefault(e => e.Bounds.Contains(x, y));
            foreach (var s in _surfaces) s.ShowWindowHighlight(w);
        }
        else if (_kind == TargetKind.Screen)
        {
            foreach (var s in _surfaces) s.ShowScreenHighlight(s.Monitor.Bounds.Contains(x, y));
        }
    }

    public void ClickAt(int x, int y)
    {
        if (_kind == TargetKind.Window)
        {
            var w = _windows.FirstOrDefault(e => e.Bounds.Contains(x, y));
            if (w == null) return;
            var m = _monitors.FirstOrDefault(s => s.Handle == Native.MonitorFromWindow(w.Handle, 2)) ?? _monitors[0];
            Finish(CaptureTarget.ForWindow(w.Handle, w.Bounds, m));
        }
        else if (_kind == TargetKind.Screen)
        {
            var m = _monitors.FirstOrDefault(s => s.Bounds.Contains(x, y));
            if (m != null) Finish(CaptureTarget.ForMonitor(m));
        }
    }

    public void Finish(CaptureTarget? t)
    {
        if (_finished) return;
        _finished = true;
        foreach (var s in _surfaces) s.Close();
        _result.TrySetResult(t);
    }
}

internal sealed class PickerSurface : Window
{
    private const int MinArea = 32;
    private readonly Picker _owner;
    private readonly TargetKind _kind;
    public MonitorInfo Monitor { get; }
    private readonly Grid _root = new();
    private readonly Path _dim;
    private readonly RectangleGeometry _hole = new();
    private readonly Rectangle _frame;
    private readonly Border _badge;
    private readonly TextBlock _badgeText;
    private readonly Border? _number;
    private double _scale = 1;
    private bool _dragging;
    private int _startX, _startY;
    private PxRect _area;
    private bool _hasArea;
    private IntPtr _hwnd;
    private bool _highlighted;

    public PickerSurface(Picker owner, TargetKind kind, MonitorInfo m, int monitorCount, PxRect? previous)
    {
        _owner = owner; _kind = kind; Monitor = m;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        Background = Brushes.Black; Title = "Flick picker"; WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; Top = -32000; Width = 100; Height = 100; ShowActivated = false;
        Cursor = kind == TargetKind.Area ? Cursors.Cross : Cursors.Hand;

        // A still of this screen, taken before anything of ours appears on it.
        var still = BitmapSource.Create(m.Bounds.W, m.Bounds.H, 96, 96, PixelFormats.Bgr32, null, Native.CopyScreen(m.Bounds), m.Bounds.W * 4);
        still.Freeze();
        _root.Children.Add(new Image { Source = still, Stretch = Stretch.Fill });
        RenderOptions.SetBitmapScalingMode(_root.Children[0], BitmapScalingMode.NearestNeighbor);

        var everything = new RectangleGeometry(new Rect(0, 0, 100000, 100000));
        _dim = new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(kind == TargetKind.Area ? (byte)120 : (byte)130, 8, 8, 12)),
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, everything, _hole),
            IsHitTestVisible = false,
        };
        _root.Children.Add(_dim);

        double corner = kind == TargetKind.Area ? OutlineWindow.CornerRadius : 8;
        _hole.RadiusX = corner; _hole.RadiusY = corner;
        _frame = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xB8)), StrokeThickness = kind == TargetKind.Area ? 2.2 : 1.5, RadiusX = corner, RadiusY = corner,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        if (kind == TargetKind.Area) { _frame.StrokeDashArray = new DoubleCollection { 0, 2.8 }; _frame.StrokeDashCap = PenLineCap.Round; }
        _root.Children.Add(_frame);

        _badgeText = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontFamily = (FontFamily)Application.Current.Resources["UiFont"] };
        System.Windows.Documents.Typography.SetNumeralAlignment(_badgeText, FontNumeralAlignment.Tabular);
        _badge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3F)), CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 3, 8, 4),
            Child = _badgeText, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        _root.Children.Add(_badge);

        if (kind == TargetKind.Screen)
        {
            _number = new Border
            {
                Width = 120, Height = 120, CornerRadius = new CornerRadius(30), Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3F)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x4C, 0x50)), BorderThickness = new Thickness(1), IsHitTestVisible = false,
                Child = new TextBlock { Text = m.Index.ToString(), FontSize = 56, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            _root.Children.Add(_number);
        }

        string text = kind switch
        {
            TargetKind.Area => "Drag to choose an area",
            TargetKind.Window => "Click a window",
            _ => "Click a screen",
        };
        _root.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3F)), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 9, 16, 10),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x4C, 0x50)), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 28, 0, 0), IsHitTestVisible = false,
            Child = new TextBlock
            {
                FontFamily = (FontFamily)Application.Current.Resources["UiFont"], FontSize = 14, Foreground = Brushes.White,
                Inlines = { new System.Windows.Documents.Run(text) { FontWeight = FontWeights.SemiBold }, new System.Windows.Documents.Run("   Esc to cancel") { Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)) } },
            },
        });

        Content = _root;
        SourceInitialized += (_, _) =>
        {
            _hwnd = Win.Float(this, noActivate: false);
            Win.Place(_hwnd, m.Bounds);
            _scale = Win.Scale(_hwnd);
            if (previous is { } prev) { _area = prev; _hasArea = true; DrawArea(prev); }
            else if (kind == TargetKind.Area) ClearHole();
            else ClearHole();
        };

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) => _owner.Finish(null);
        KeyDown += OnKey;
    }

    /// <summary>Test support: the same drag logic the mouse uses, on a surface that is never shown.</summary>
    internal (PxRect Area, Rect Local, bool Ok) TestDrag(int x1, int y1, int x2, int y2)
    {
        _startX = x1; _startY = y1;
        Move(x2, y2);
        return (_area, _hole.Rect, _hasArea);
    }

    public void TakeFocus()
    {
        Activate();
        Native.SetForegroundWindow(_hwnd);
        Focus();
        Keyboard.Focus(this);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) _owner.Finish(null);
        else if (e.Key == Key.Enter && _hasArea && _kind == TargetKind.Area) _owner.Finish(CaptureTarget.ForArea(_area, Monitor));
    }

    private static (int X, int Y) Cursor_() { Native.GetCursorPos(out var c); return (c.X, c.Y); }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var (x, y) = Cursor_();
        if (_kind != TargetKind.Area) { _owner.ClickAt(x, y); return; }
        _dragging = true;
        _startX = x; _startY = y;
        CaptureMouse();
        Move(x, y);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var (x, y) = Cursor_();
        if (_kind != TargetKind.Area) { _owner.UpdateHover(x, y); return; }
        if (_dragging) Move(x, y);
    }

    private void Move(int x, int y)
    {
        var b = Monitor.Bounds;
        x = Math.Clamp(x, b.X, b.Right); y = Math.Clamp(y, b.Y, b.Bottom);
        int left = Math.Min(x, _startX), top = Math.Min(y, _startY);
        _area = new PxRect(left, top, Math.Abs(x - _startX), Math.Abs(y - _startY));
        _hasArea = _area.W >= MinArea && _area.H >= MinArea;
        DrawArea(_area);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (_hasArea) _owner.Finish(CaptureTarget.ForArea(_area, Monitor));
        else ClearHole(); // a stray click: stay open and let them try again
    }

    private Rect ToLocal(PxRect r) => new((r.X - Monitor.Bounds.X) / _scale, (r.Y - Monitor.Bounds.Y) / _scale, r.W / _scale, r.H / _scale);

    private void DrawArea(PxRect r)
    {
        var l = ToLocal(r);
        _hole.Rect = l;
        _frame.Width = l.Width; _frame.Height = l.Height; _frame.Margin = new Thickness(l.X, l.Y, 0, 0);
        _frame.Visibility = Visibility.Visible;
        _badgeText.Text = $"{r.W} × {r.H}";
        double by = l.Bottom + 8 + 28 > ActualHeight ? Math.Max(0, l.Bottom - 34) : l.Bottom + 8;
        _badge.Margin = new Thickness(l.X, by, 0, 0);
        _badge.Visibility = Visibility.Visible;
    }

    private void ClearHole()
    {
        _hole.Rect = Rect.Empty;
        _frame.Visibility = Visibility.Collapsed;
        _badge.Visibility = Visibility.Collapsed;
    }

    public void ShowWindowHighlight(Native.WindowEntry? w)
    {
        if (w == null || !Overlaps(w.Bounds, Monitor.Bounds)) { ClearHole(); return; }
        var l = ToLocal(w.Bounds);
        _hole.Rect = l;
        _frame.Width = Math.Max(0, l.Width); _frame.Height = Math.Max(0, l.Height); _frame.Margin = new Thickness(l.X, l.Y, 0, 0);
        _frame.Visibility = Visibility.Visible;
        _badgeText.Text = w.Title.Length > 60 ? w.Title[..60] + "…" : w.Title;
        _badge.Margin = new Thickness(Math.Max(8, l.X + 8), Math.Max(8, l.Y + 8), 0, 0);
        _badge.Visibility = Visibility.Visible;
    }

    public void ShowScreenHighlight(bool on)
    {
        if (on == _highlighted) return;
        _highlighted = on;
        _hole.Rect = on ? new Rect(0, 0, 100000, 100000) : Rect.Empty;
    }

    private static bool Overlaps(PxRect a, PxRect b) => a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;
}
