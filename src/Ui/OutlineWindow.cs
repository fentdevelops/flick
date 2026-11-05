using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Flick;

/// <summary>
/// The dotted, rounded line around the chosen area, with a small control at its top right to move and resize it.
/// Everything inside the line is fully transparent, so clicks reach the apps underneath. While recording it becomes
/// click-through with no control, and like all of Flick's windows it never appears in captures.
/// </summary>
internal sealed class OutlineWindow : Window
{
    public const int MinSize = 32;
    public const double CornerRadius = 16;
    private const double DotSpacing = 6.2; // same for the dots and their halo, so they stay lined up
    private const double Pad = 4, ControlHeight = 34, ControlGap = 6, ControlReserve = 206;

    private enum DragMode { None, Move, Resize }

    /// <summary>The area changed. The flag is true when the drag has ended.</summary>
    public event Action<PxRect, MonitorInfo, bool>? AreaChanged;

    /// <summary>The user pressed the X: forget this area.</summary>
    public event Action? Removed;

    private readonly Rectangle _halo, _line;
    private readonly Border _control;
    private readonly TextBlock _size;
    private IntPtr _hwnd;
    private PxRect _area;
    private MonitorInfo? _monitor;
    private bool _interactive;
    private DragMode _drag;
    private Native.POINT _dragFrom;
    private PxRect _dragStart;

    public OutlineWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        ShowActivated = false; ResizeMode = ResizeMode.NoResize; Title = "Flick area"; Left = -32000; Top = -32000; Width = 10; Height = 10;

        // A dark halo under the grey dots keeps the line visible on light and dark screens alike.
        _halo = MakeLine(Color.FromArgb(120, 0, 0, 0), 3.8);
        _line = MakeLine(Color.FromRgb(0xB4, 0xB4, 0xB8), 2.2);

        _size = new TextBlock
        {
            Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(9, 0, 5, 1), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(_size, FontNumeralAlignment.Tabular);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_size);
        row.Children.Add(MakeHandle("IconMove", "Drag to move", Cursors.SizeAll, DragMode.Move));
        row.Children.Add(MakeHandle("IconResize", "Drag to resize", Cursors.SizeNESW, DragMode.Resize));
        row.Children.Add(MakeRemove());

        _control = new Border
        {
            Height = ControlHeight, Padding = new Thickness(3), CornerRadius = new CornerRadius(12), Child = row,
            Background = (Brush)Application.Current.Resources["PanelFill"], BorderBrush = (Brush)Application.Current.Resources["PanelRim"], BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        };

        var root = new Grid();
        root.Children.Add(_halo);
        root.Children.Add(_line);
        root.Children.Add(_control);
        Content = root;

        SourceInitialized += (_, _) => _hwnd = Win.Float(this, noActivate: true);
    }

    private static Rectangle MakeLine(Color color, double thickness) => new()
    {
        Stroke = new SolidColorBrush(color), StrokeThickness = thickness, RadiusX = CornerRadius, RadiusY = CornerRadius,
        StrokeDashArray = new DoubleCollection { 0, DotSpacing / thickness }, StrokeDashCap = PenLineCap.Round,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
    };

    private Grid MakeHandle(string icon, string tip, Cursor cursor, DragMode mode)
    {
        var hover = new Border { Margin = new Thickness(2), CornerRadius = new CornerRadius(7), Background = (Brush)Application.Current.Resources["Fill2"], Opacity = 0 };
        var glyph = new Path
        {
            Data = (Geometry)Application.Current.Resources[icon], Stroke = Brushes.White, StrokeThickness = 1.9, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Width = 24, Height = 24, Stretch = Stretch.None, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.72, 0.72),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var handle = new Grid { Width = 30, Height = 26, Background = Brushes.Transparent, Cursor = cursor, ToolTip = tip };
        handle.Children.Add(hover);
        handle.Children.Add(glyph);
        handle.MouseEnter += (_, _) => Anim.Fade(hover, 1, 550);
        handle.MouseLeave += (_, _) => Anim.Fade(hover, 0, 550);
        handle.MouseLeftButtonDown += (_, e) =>
        {
            Native.GetCursorPos(out _dragFrom);
            _dragStart = _area;
            _drag = mode;
            handle.CaptureMouse();
            ShowSize();
            e.Handled = true;
        };
        handle.MouseMove += (_, _) =>
        {
            if (_drag != mode) return;
            Native.GetCursorPos(out var c);
            DragTo(c.X, c.Y, done: false);
        };
        handle.MouseLeftButtonUp += (_, _) =>
        {
            if (_drag != mode) return;
            handle.ReleaseMouseCapture();
            EndDrag();
        };
        handle.LostMouseCapture += (_, _) => { if (_drag == mode) EndDrag(); };
        return handle;
    }

    private Grid MakeRemove()
    {
        var hover = new Border { Margin = new Thickness(2), CornerRadius = new CornerRadius(7), Background = (Brush)Application.Current.Resources["Fill2"], Opacity = 0 };
        var glyph = new Path
        {
            Data = (Geometry)Application.Current.Resources["IconClose"], Stroke = Brushes.White, StrokeThickness = 1.9, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 24, Height = 24, Stretch = Stretch.None, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.72, 0.72),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var button = new Grid { Width = 30, Height = 26, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Remove this area" };
        button.Children.Add(hover);
        button.Children.Add(glyph);
        button.MouseEnter += (_, _) => Anim.Fade(hover, 1, 550);
        button.MouseLeave += (_, _) => Anim.Fade(hover, 0, 550);
        button.MouseLeftButtonUp += (_, _) => Remove();
        return button;
    }

    private void Remove()
    {
        HideOutline();
        Removed?.Invoke();
    }

    private void ShowSize()
    {
        _size.Text = $"{_area.W} × {_area.H}";
        _size.Visibility = Visibility.Visible;
    }

    private void EndDrag()
    {
        _drag = DragMode.None;
        _size.Visibility = Visibility.Collapsed;
        if (_monitor != null) AreaChanged?.Invoke(_area, _monitor, true);
    }

    private void DragTo(int cursorX, int cursorY, bool done)
    {
        if (_monitor == null) return;
        int dx = cursorX - _dragFrom.X, dy = cursorY - _dragFrom.Y;
        if (_drag == DragMode.Move)
        {
            // Moving follows the cursor onto another screen.
            var under = Native.Monitors().FirstOrDefault(m => m.Bounds.Contains(cursorX, cursorY));
            if (under != null) _monitor = under;
            _area = MovedArea(_dragStart, _monitor.Bounds, dx, dy);
        }
        else _area = ResizedArea(_dragStart, _monitor.Bounds, dx, dy);
        Arrange_();
        ShowSize();
        AreaChanged?.Invoke(_area, _monitor, done);
    }

    /// <summary>The area moved by the drag, kept fully on the screen.</summary>
    public static PxRect MovedArea(PxRect start, PxRect screen, int dx, int dy)
    {
        int w = Math.Min(start.W, screen.W), h = Math.Min(start.H, screen.H);
        return new PxRect(Math.Clamp(start.X + dx, screen.X, screen.Right - w), Math.Clamp(start.Y + dy, screen.Y, screen.Bottom - h), w, h);
    }

    /// <summary>The area resized from its top-right corner. The bottom-left corner stays put.</summary>
    public static PxRect ResizedArea(PxRect start, PxRect screen, int dx, int dy)
    {
        int right = Math.Clamp(start.Right + dx, start.X + MinSize, screen.Right);
        int top = Math.Clamp(start.Y + dy, screen.Y, start.Bottom - MinSize);
        return new PxRect(start.X, top, right - start.X, start.Bottom - top);
    }

    public void ShowAround(PxRect area, IntPtr monitor, bool interactive)
    {
        if (_drag != DragMode.None) return;
        _area = area;
        _monitor = Native.Monitors().FirstOrDefault(m => m.Handle == monitor) ?? Native.Monitors().FirstOrDefault(m => m.Bounds.Contains(area.X, area.Y));
        _interactive = interactive;
        if (_hwnd == IntPtr.Zero) Show();

        // While recording, nothing here should get in the way of the mouse.
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, interactive ? ex & ~Native.WS_EX_TRANSPARENT : ex | Native.WS_EX_TRANSPARENT);
        _control.Visibility = interactive ? Visibility.Visible : Visibility.Collapsed;

        Arrange_();
        if (!IsVisible) Show();
    }

    /// <summary>Fits the window around the area, leaving room for the control above it, or inside if there's no room above.</summary>
    private void Arrange_()
    {
        if (_monitor == null) return;
        double s = Native.MonitorScale(_monitor.Handle);
        int pad = (int)Math.Ceiling(Pad * s);
        bool above = (_area.Y - _monitor.Bounds.Y) / s >= ControlHeight + ControlGap + Pad;
        int topPx = _interactive && above ? (int)Math.Ceiling((ControlHeight + ControlGap) * s) : 0;
        int leftPx = _interactive ? Math.Max(0, (int)Math.Ceiling(ControlReserve * s) - _area.W) : 0;
        var rect = new PxRect(_area.X - pad - leftPx, _area.Y - pad - topPx, _area.W + 2 * pad + leftPx, _area.H + 2 * pad + topPx);

        Win.Place(_hwnd, rect);
        // Crossing onto a screen with different scaling changes the window's own scale, which can change its size.
        if (Math.Abs(Win.Scale(_hwnd) - s) > 0.001) { s = Win.Scale(_hwnd); }
        Win.Place(_hwnd, rect);

        double lineW = (_area.W + 2 * pad) / s, lineH = (_area.H + 2 * pad) / s, lineX = leftPx / s, lineY = topPx / s;
        foreach (var l in new[] { _halo, _line })
        {
            l.Width = lineW; l.Height = lineH;
            l.Margin = new Thickness(lineX, lineY, 0, 0);
        }
        _control.Margin = above ? new Thickness(0) : new Thickness(0, pad / s + ControlGap, pad / s + ControlGap, 0);
    }

    public void HideOutline()
    {
        if (_drag != DragMode.None) return;
        if (IsVisible) Hide();
    }

    /// <summary>For the README picture: the line and control laid out without a window.</summary>
    internal FrameworkElement DetachForPicture(double w, double h)
    {
        var root = (Grid)Content;
        Content = null;
        foreach (var l in new[] { _halo, _line })
        {
            l.Width = w; l.Height = h;
            l.Margin = new Thickness(0, ControlHeight + ControlGap, 0, 0);
        }
        root.Width = w;
        root.Height = h + ControlHeight + ControlGap;
        root.HorizontalAlignment = HorizontalAlignment.Left;
        root.VerticalAlignment = VerticalAlignment.Top;
        return root;
    }

    // ---- Test support ----

    internal void TestRemove() => Remove();

    internal bool TestClickThrough => (Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TRANSPARENT) != 0;
    internal bool TestControlVisible => _control.Visibility == Visibility.Visible;
    internal IntPtr TestHandle => _hwnd;
    internal PxRect TestArea => _area;
    internal bool TestDotted => _line.StrokeDashArray.Count > 0 && _line.RadiusX > 0;

    internal void TestDrag(bool move, int dx, int dy)
    {
        _dragFrom = new Native.POINT { X = _area.X, Y = _area.Y };
        _dragStart = _area;
        _drag = move ? DragMode.Move : DragMode.Resize;
        DragTo(_area.X + dx, _area.Y + dy, done: false);
        EndDrag();
    }
}
