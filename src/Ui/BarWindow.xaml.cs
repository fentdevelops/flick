using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Flick;

public partial class BarWindow : Window
{
    private enum State { Idle, Picking, Starting, Recording, Saving }

    private const int HotkeyId = 0xF11C;
    private const double BarH = 70, EdgeGap = 24, StackGap = 12;

    private readonly Settings _s = Settings.Load();
    private readonly List<CardModel> _cards = Quality.All.Select(q => new CardModel(q)).ToList();
    private readonly PillWindow _pill = new();
    private readonly OutlineWindow _outline = new();
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private IntPtr _hwnd;
    private State _state = State.Idle;
    private CaptureTarget? _target;
    private Recorder? _recorder;
    private int _previewGen;
    private Preview.Result? _preview;
    private bool _dragging, _dragMoved;
    private Native.POINT _dragOffset;
    private bool _flipped;
    private bool _shown;
    private string? _lastFile;
    private Tray? _tray;
    private bool _exiting;

    public BarWindow()
    {
        InitializeComponent();
        Cards.ItemsSource = _cards;
        SourceInitialized += OnSourceInitialized;
        _toastTimer.Tick += (_, _) => HideToast();
        _pill.StopRequested += StopRecording;
        _outline.AreaChanged += OnAreaChanged;
        _outline.Removed += OnAreaRemoved;
        ApplySettings();
    }

    // ---- Start-up ----

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = Win.Float(this, noActivate: true);
        var src = HwndSource.FromHwnd(_hwnd)!;
        src.AddHook(WndProc);
        if (!Native.RegisterHotKey(_hwnd, HotkeyId, Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT, 0x35 /*5*/))
            Log.Write("Ctrl+Shift+5 is already taken by another app");
        _tray = new Tray(_hwnd, this);
    }

    private IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && w.ToInt32() == HotkeyId) { handled = true; OnHotkey(); }
        else if (msg == Tray.Message) { handled = true; _tray?.OnMessage(l.ToInt32()); }
        else if (msg == Native.WM_DISPLAYCHANGE) Dispatcher.BeginInvoke(new Action(OnDisplayChanged));
        return IntPtr.Zero;
    }

    public void ShowFirstTime()
    {
        ShowBar();
        if (!_s.SeenGuide) ShowGuide();
    }

    private void ApplySettings()
    {
        SetMode(_s.Record, instant: true);
        SetTargetKind(_s.Target, instant: true);
        foreach (var c in _cards) c.Chosen = c.Name == _s.Quality;
        QualityButton.Word = Quality.ByName(_s.Quality).Name;
        UpdateSound();
        RefreshCards();
    }

    private void OnDisplayChanged()
    {
        if (_target != null && !_target.StillValid()) _target = null;
        if (_shown && _state == State.Idle) { PlaceBar(); RefreshTargetVisuals(); RefreshPreview(); }
    }

    // ---- Show, hide, position ----

    public void ShowBar()
    {
        if (_state is State.Recording or State.Starting or State.Saving) return;
        Show();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        PlaceBar();
        _shown = true;
        Anim.Fade(Root, 1, 450);
        Anim.To(BarSlide, System.Windows.Media.TranslateTransform.YProperty, 0, 650);
        LayoutStack();
        RefreshTargetVisuals();
        RefreshPreview();
    }

    private void HideBar(bool andForget = false, double ms = 280, Action? done = null)
    {
        _shown = false;
        _outline.HideOutline();
        HideToast();
        Anim.To(BarSlide, System.Windows.Media.TranslateTransform.YProperty, _flipped ? -14 : 14, ms);
        Anim.Fade(Root, 0, ms, () =>
        {
            if (!_shown) Hide();
            done?.Invoke();
        });
    }

    public void ToggleBar()
    {
        if (_shown) HideBar(); else ShowBar();
    }

    private void OnHotkey()
    {
        switch (_state)
        {
            case State.Recording: StopRecording(); break;
            case State.Idle: ToggleBar(); break;
        }
    }

    /// <summary>Puts the bar on the chosen spot, or centred low on the screen the cursor is on.</summary>
    private void PlaceBar()
    {
        double s = Win.Scale(_hwnd);
        var monitors = Native.Monitors();
        Native.GetCursorPos(out var c);
        int barX, barY;
        int barW = (int)(Bar.Width * s), barH = (int)(BarH * s);

        if (_s.BarX is { } x && _s.BarY is { } y && monitors.Any(m => m.Bounds.Contains(x + barW / 2, y + barH / 2)))
        {
            barX = x; barY = y;
        }
        else
        {
            var work = Native.WorkArea(Native.MonitorAt(c.X, c.Y));
            s = Native.MonitorScale(Native.MonitorAt(c.X, c.Y));
            barW = (int)(Bar.Width * s); barH = (int)(BarH * s);
            barX = work.X + (work.W - barW) / 2;
            barY = work.Bottom - barH - (int)(28 * s);
        }
        PositionForBar(barX, barY);
    }

    /// <summary>The window is bigger than the bar, leaving room for the panels. They open upward, or downward near the top of a screen.</summary>
    private void PositionForBar(int barX, int barY)
    {
        double s = Win.Scale(_hwnd);
        int winW = (int)(Width * s), winH = (int)(Height * s), edge = (int)(EdgeGap * s), barH = (int)(BarH * s), barW = (int)(Bar.Width * s);
        var work = Native.WorkArea(Native.MonitorAt(barX + barW / 2, barY + barH / 2));
        bool flip = barY - (winH - barH - edge) < work.Y;
        if (flip != _flipped) { _flipped = flip; LayoutStack(); }
        int wx = barX - (winW - barW) / 2;
        int wy = flip ? barY - edge : barY + barH + edge - winH;
        Win.Move(_hwnd, wx, wy);
    }

    private (int X, int Y) BarOrigin()
    {
        Native.GetWindowRect(_hwnd, out var r);
        double s = Win.Scale(_hwnd);
        int winW = r.Right - r.Left, winH = r.Bottom - r.Top, edge = (int)(EdgeGap * s), barH = (int)(BarH * s), barW = (int)(Bar.Width * s);
        return (r.Left + (winW - barW) / 2, _flipped ? r.Top + edge : r.Bottom - edge - barH);
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        Native.GetCursorPos(out var c);
        var (bx, by) = BarOrigin();
        _dragOffset = new Native.POINT { X = c.X - bx, Y = c.Y - by };
        _dragging = true; _dragMoved = false;
        BarBody.CaptureMouse();
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        Native.GetCursorPos(out var c);
        _dragMoved = true;
        PositionForBar(c.X - _dragOffset.X, c.Y - _dragOffset.Y);
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        BarBody.ReleaseMouseCapture();
        if (_dragMoved)
        {
            var (x, y) = BarOrigin();
            _s.BarX = x; _s.BarY = y; _s.Save();
            if (_target?.Kind == TargetKind.Screen) { /* an explicit screen choice stays as it is */ }
            else if (_s.Target == TargetKind.Screen) { _target = null; RefreshPreview(); }
        }
    }

    /// <summary>Stacks strip, guide and toast next to the bar, gliding when something opens or closes.</summary>
    private void LayoutStack()
    {
        var edge = _flipped ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        Bar.VerticalAlignment = edge;
        Bar.Margin = _flipped ? new Thickness(0, EdgeGap, 0, 0) : new Thickness(0, 0, 0, EdgeGap);

        double offset = EdgeGap + BarH + StackGap;
        foreach (var panel in new FrameworkElement[] { Guide, Strip })
        {
            panel.VerticalAlignment = edge;
            panel.Margin = _flipped ? new Thickness(0, offset, 0, 0) : new Thickness(0, 0, 0, offset);
        }
        Toast.VerticalAlignment = edge;
        UpdateLayout();

        double stacked = offset;
        if (Guide.Visibility == Visibility.Visible && GuideWanted) stacked += Guide.ActualHeight + StackGap;
        else if (Strip.Visibility == Visibility.Visible && _s.QualityOpen) stacked += Strip.ActualHeight + StackGap;

        double previous = Toast.Margin.Top + Toast.Margin.Bottom;
        Toast.Margin = _flipped ? new Thickness(0, stacked, 0, 0) : new Thickness(0, 0, 0, stacked);
        if (previous > 0 && Toast.Visibility == Visibility.Visible && Math.Abs(previous - stacked) > 0.5)
        {
            double from = _flipped ? previous - stacked : stacked - previous;
            Anim.Set(ToastSlide, System.Windows.Media.TranslateTransform.YProperty, from);
            Anim.To(ToastSlide, System.Windows.Media.TranslateTransform.YProperty, 0, 600);
        }
    }

    // ---- Modes and options ----

    private void OnModeShot(object s, RoutedEventArgs e) => SetMode(false);
    private void OnModeRecord(object s, RoutedEventArgs e) => SetMode(true);

    private void SetMode(bool record, bool instant = false)
    {
        bool changed = _s.Record != record;
        _s.Record = record;
        ModeShot.IsChecked = !record;
        ModeRecord.IsChecked = record;
        SlideTo(ModeSlide, record ? 74 : 0, instant);
        SoundButton.IsEnabled = record;
        GoText.Text = record ? "Record" : "Capture";
        GoDot.Visibility = record ? Visibility.Visible : Visibility.Collapsed;
        UpdateSound();
        RefreshCards();
        if (changed && !instant) { _s.Save(); RefreshPreviewIfOpen(); }
    }

    private static void SlideTo(System.Windows.Media.TranslateTransform t, double x, bool instant)
    {
        if (instant) { t.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null); t.X = x; }
        else Anim.To(t, System.Windows.Media.TranslateTransform.XProperty, x, 700);
    }

    private void SetTargetKind(TargetKind k, bool instant = false)
    {
        _s.Target = k;
        TargetScreen.IsChecked = k == TargetKind.Screen;
        TargetWindow.IsChecked = k == TargetKind.Window;
        TargetArea.IsChecked = k == TargetKind.Area;
        SlideTo(TargetSlide, (int)k * 62, instant);
        if (!instant) _s.Save();
    }

    private async void OnTargetScreen(object s, RoutedEventArgs e)
    {
        if (_state != State.Idle) return;
        SetTargetKind(TargetKind.Screen);
        // With one screen there's nothing to pick. With several, ask which.
        if (Native.Monitors().Count > 1) await PickTarget(TargetKind.Screen);
        else { _target = null; RefreshTargetVisuals(); RefreshPreview(); }
    }

    private async void OnTargetWindow(object s, RoutedEventArgs e)
    {
        if (_state != State.Idle) return;
        SetTargetKind(TargetKind.Window);
        await PickTarget(TargetKind.Window);
    }

    private async void OnTargetArea(object s, RoutedEventArgs e)
    {
        if (_state != State.Idle) return;
        SetTargetKind(TargetKind.Area);
        await PickTarget(TargetKind.Area);
    }

    /// <summary>Opens the chooser. Returns false if the user cancelled.</summary>
    private async Task<bool> PickTarget(TargetKind kind)
    {
        if (_state != State.Idle) return false;
        _state = State.Picking;
        _outline.HideOutline();
        Anim.Set(Root, OpacityProperty, 0);
        CaptureTarget? picked = null;
        try { picked = await Picker.Show(kind, _target); }
        catch (Exception e) { Log.Write("picker failed: " + e); ShowError("Couldn't open the chooser", e.Message); }
        _state = State.Idle;
        if (picked != null) _target = picked;
        Anim.Fade(Root, 1, 350);
        RefreshTargetVisuals();
        RefreshPreview();
        return picked != null;
    }

    private void OnQualityToggle(object s, RoutedEventArgs e)
    {
        _s.QualityOpen = !_s.QualityOpen;
        _s.Save();
        UpdateStrip();
        if (_s.QualityOpen) RefreshPreview();
    }

    private void OnSoundToggle(object s, RoutedEventArgs e)
    {
        _s.Sound = !_s.Sound;
        _s.Save();
        UpdateSound();
        RefreshCards();
    }

    private void UpdateSound()
    {
        SoundButton.Icon = (Geometry)FindResource(_s.Sound ? "IconSoundOn" : "IconSoundOff");
        SoundButton.Word = _s.Sound ? "Sound on" : "Sound off";
        SoundButton.IsChecked = _s.Sound;
        SoundButton.ToolTip = !_s.Record ? "Screenshots have no sound" : _s.Sound ? "Recording what your computer plays. Click to turn off." : "No sound in the video. Click to turn on.";
    }

    private void OnCard(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CardModel card }) return;
        _s.Quality = card.Name;
        _s.Save();
        foreach (var c in _cards) c.Chosen = c == card;
        // A ToggleButton flips itself on click; keep it matching the model.
        if (sender is System.Windows.Controls.Primitives.ToggleButton t) t.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
        QualityButton.Word = card.Name;
        // Picked: the strip has done its job. The Quality button opens it again.
        _s.QualityOpen = false;
        _s.Save();
        UpdateStrip();
    }

    // ---- What's being captured ----

    private MonitorInfo BarMonitor()
    {
        var all = Native.Monitors();
        IntPtr h = _hwnd != IntPtr.Zero ? Native.MonitorFromWindow(_hwnd, 2) : IntPtr.Zero;
        var (bx, by) = _hwnd != IntPtr.Zero ? BarOrigin() : (0, 0);
        var at = all.FirstOrDefault(m => m.Bounds.Contains(bx + 50, by + 20));
        return at ?? all.FirstOrDefault(m => m.Handle == h) ?? all.First(m => m.Primary);
    }

    /// <summary>The target as it stands now, or null if it still has to be picked.</summary>
    private CaptureTarget? CurrentTarget()
    {
        switch (_s.Target)
        {
            case TargetKind.Screen:
                if (_target?.Kind == TargetKind.Screen && _target.StillValid()) return _target;
                return CaptureTarget.ForMonitor(BarMonitor());
            case TargetKind.Window:
                if (_target?.Kind == TargetKind.Window && _target.StillValid() && !Native.IsIconic(_target.Window))
                {
                    var b = Native.VisibleWindowBounds(_target.Window);
                    return b is { } bounds ? _target with { Bounds = bounds } : _target;
                }
                return null;
            default:
                return _target?.Kind == TargetKind.Area && _target.StillValid() ? _target : null;
        }
    }

    private void RefreshTargetVisuals()
    {
        var t = CurrentTarget();
        if (_shown && t?.Kind == TargetKind.Area && _state == State.Idle) _outline.ShowAround(t.Bounds, t.Monitor, interactive: true);
        else if (_state != State.Recording && _state != State.Starting) _outline.HideOutline();
        TargetScreen.Word = "Screen";
        if (_s.Target == TargetKind.Screen && Native.Monitors().Count > 1 && t != null)
        {
            var m = Native.Monitors().FirstOrDefault(x => x.Handle == t.Monitor);
            if (m != null) TargetScreen.Word = $"Screen {m.Index}";
        }
        RefreshCards();
    }

    private void OnAreaChanged(PxRect area, MonitorInfo monitor, bool done)
    {
        if (_state != State.Idle) return;
        _target = CaptureTarget.ForArea(area, monitor);
        if (done) RefreshPreviewIfOpen();
    }

    /// <summary>The area is gone. Area stays selected on the bar, so pressing the main button asks for a new one.</summary>
    private void OnAreaRemoved()
    {
        if (_state != State.Idle) return;
        _target = null;
        RefreshCards();
        RefreshPreviewIfOpen();
    }

    private void RefreshPreviewIfOpen() { if (_s.QualityOpen) RefreshPreview(); else RefreshCards(); }

    /// <summary>Grabs the target and rebuilds the example pictures off the UI thread. Only the newest request counts.</summary>
    private async void RefreshPreview()
    {
        UpdateStrip();
        if (!_shown || !_s.QualityOpen || GuideWanted) return;
        var t = CurrentTarget() ?? CaptureTarget.ForMonitor(BarMonitor());
        int gen = ++_previewGen;
        double scale = _hwnd != IntPtr.Zero ? Win.Scale(_hwnd) : 1;
        bool measureShots = !_s.Record;
        try
        {
            var result = await Bg.Sta(() => Preview.Build(Snap.CaptureForPreview(t), scale, measureShots));
            if (gen != _previewGen) return;
            _preview = result;
            ApplyPreview(result);
            // A full-screen grab is tens of MB. Hand it back now rather than whenever.
            GC.Collect(2, GCCollectionMode.Optimized, false);
        }
        catch (Exception e) { Log.Write("preview failed: " + e.Message); }
    }

    public void ApplyPreview(Preview.Result r)
    {
        _preview = r;
        for (int i = 0; i < _cards.Count; i++) _cards[i].Image = r.Images[i];
        RefreshCards();
    }

    /// <summary>Fills in each level's numbers for the current mode, target and sound setting.</summary>
    private void RefreshCards()
    {
        var t = CurrentTarget() ?? (_hwnd != IntPtr.Zero ? CaptureTarget.ForMonitor(BarMonitor()) : null);
        int w = t?.Bounds.W ?? 1920, h = t?.Bounds.H ?? 1080;

        if (_s.Record)
        {
            StripTitle.Text = "Video quality";
            StripNote.Text = "Examples from your screen. Calm screens come out much smaller than these sizes.";
            foreach (var c in _cards)
            {
                var q = c.Quality;
                c.Big = $"Up to {Fmt.Size(q.EstimateBytes(w, h, 60, _s.Sound))} a minute";
                c.Small = $"10s {Fmt.Size(q.EstimateBytes(w, h, 10, _s.Sound))} · 5m {Fmt.Size(q.EstimateBytes(w, h, 300, _s.Sound))}";
                c.Detail = q.Detail(w, h);
            }
        }
        else
        {
            StripTitle.Text = "Screenshot quality";
            StripNote.Text = "Examples and sizes from your screen right now.";
            for (int i = 0; i < _cards.Count; i++)
            {
                var c = _cards[i];
                var q = c.Quality;
                bool known = _preview != null && _preview.ShotBytes[i] > 0;
                c.Big = known ? $"About {Fmt.Size(_preview!.ShotBytes[i])}" : "Working it out";
                c.Small = q.ShotJpeg > 0 ? "JPG picture" : "PNG picture, no loss";
                c.Detail = known ? $"{_preview!.ShotW[i]}×{_preview.ShotH[i]}" : "";
            }
        }
    }

    // ---- Panels ----

    private bool GuideWanted => _guideOpen;
    private bool _guideOpen;

    private void UpdateStrip()
    {
        bool want = _s.QualityOpen && !_guideOpen;
        QualityButton.IsChecked = _s.QualityOpen;
        ShowPanel(Strip, StripSlide, want);
    }

    private void ShowPanel(FrameworkElement panel, System.Windows.Media.TranslateTransform slide, bool show)
    {
        panel.IsHitTestVisible = show;
        if (show)
        {
            bool wasHidden = panel.Visibility != Visibility.Visible;
            panel.Visibility = Visibility.Visible;
            if (wasHidden) Anim.Set(slide, System.Windows.Media.TranslateTransform.YProperty, _flipped ? -10 : 10);
            Anim.Fade(panel, 1, 450);
            Anim.To(slide, System.Windows.Media.TranslateTransform.YProperty, 0, 600);
            LayoutStack();
        }
        else if (panel.Visibility == Visibility.Visible)
        {
            Anim.To(slide, System.Windows.Media.TranslateTransform.YProperty, _flipped ? -10 : 10, 400);
            Anim.Fade(panel, 0, 320, () =>
            {
                if (!panel.IsHitTestVisible) panel.Visibility = Visibility.Collapsed;
            });
            LayoutStackHiding(panel);
        }
    }

    private void LayoutStackHiding(FrameworkElement panel)
    {
        // Same as LayoutStack, counting the closing panel as already gone so the toast glides in step.
        var was = panel.Visibility;
        panel.Visibility = Visibility.Collapsed;
        LayoutStack();
        panel.Visibility = was;
    }

    public void ShowGuide()
    {
        _guideOpen = true;
        ShowPanel(Guide, GuideSlide, true);
        UpdateStrip();
    }

    private void OnGuideDone(object s, RoutedEventArgs e)
    {
        _s.SeenGuide = true;
        _s.Save();
        _guideOpen = false;
        ShowPanel(Guide, GuideSlide, false);
        RefreshPreview();
    }

    private void ShowToast(string title, string body, BitmapSource? thumb, string? file, bool ok = true)
    {
        ToastTitle.Text = title;
        ToastBody.Text = body;
        ToastThumb.Source = thumb;
        ToastThumbFrame.Visibility = thumb != null ? Visibility.Visible : Visibility.Collapsed;
        ToastIcon.Data = (Geometry)FindResource(ok ? "IconCheck" : "IconWarn");
        ToastIcon.Stroke = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xCC));
        _lastFile = file;
        ToastOpen.Visibility = file != null ? Visibility.Visible : Visibility.Collapsed;
        Toast.Visibility = Visibility.Visible;
        LayoutStack();
        Anim.Set(ToastSlide, System.Windows.Media.TranslateTransform.YProperty, _flipped ? -10 : 10);
        Anim.Fade(Toast, 1, 450);
        Anim.To(ToastSlide, System.Windows.Media.TranslateTransform.YProperty, 0, 600);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        if (Toast.Visibility != Visibility.Visible) return;
        Anim.Fade(Toast, 0, 400, () => { if (Toast.Opacity < 0.01) Toast.Visibility = Visibility.Collapsed; });
    }

    private void ShowError(string title, string body) => ShowToast(title, body, null, null, ok: false);

    private void OnToastEnter(object s, MouseEventArgs e) => _toastTimer.Stop();
    private void OnToastLeave(object s, MouseEventArgs e) { _toastTimer.Stop(); _toastTimer.Start(); }

    private void OnOpenFolder(object s, RoutedEventArgs e)
    {
        if (_lastFile == null) return;
        try { Process.Start("explorer.exe", $"/select,\"{_lastFile}\""); }
        catch (Exception ex) { Log.Write("open folder failed: " + ex.Message); }
    }

    private void OnClose(object s, RoutedEventArgs e)
    {
        HideBar();
        if (!_s.SeenTrayHint)
        {
            _s.SeenTrayHint = true; _s.Save();
            _tray?.Balloon("Flick is still here", "Press Ctrl+Shift+5 or click the icon in the tray to bring it back.");
        }
    }

    // ---- Capture ----

    private async void OnGo(object sender, RoutedEventArgs e)
    {
        if (_state != State.Idle) return;
        if (_guideOpen) OnGuideDone(sender, e);
        var t = CurrentTarget();
        if (t == null)
        {
            if (_s.Target == TargetKind.Window && _target != null) ShowError("That window has closed or is minimized", "Pick it again.");
            if (!await PickTarget(_s.Target)) return;
            t = CurrentTarget();
            if (t == null) return;
        }
        if (_s.Record) await StartRecording(t);
        else await TakeScreenshot(t);
    }

    private async Task TakeScreenshot(CaptureTarget t)
    {
        try
        {
            var q = Quality.ByName(_s.Quality);
            var (shot, file) = await Bg.Sta(() =>
            {
                var enc = Snap.Encode(Snap.Capture(t), q);
                string f = Paths.Unique(Paths.Shots, "Flick", enc.Extension);
                File.WriteAllBytes(f, enc.Bytes);
                return (enc, f);
            });
            Snap.CopyShot(shot.Picture, file);
            ShowToast("Copied. Press Ctrl+V to paste.", $"Screenshot · {shot.Picture.PixelWidth}×{shot.Picture.PixelHeight} · {Fmt.Size(shot.Bytes.Length)}", shot.Picture, file);
        }
        catch (Exception e)
        {
            Log.Write("screenshot failed: " + e);
            ShowError("Couldn't take the screenshot", Plain(e));
        }
    }

    private async Task StartRecording(CaptureTarget t)
    {
        _state = State.Starting;
        string path;
        try { path = Paths.Unique(Paths.Clips, "Flick", ".mp4"); }
        catch (Exception e) { _state = State.Idle; ShowError("Can't save to your Videos folder", Plain(e)); return; }

        var rec = new Recorder(new RecordOptions(t, Quality.ByName(_s.Quality), _s.Sound, path));
        _recorder = rec;
        rec.Start();
        try
        {
            await rec.Started;
        }
        catch (Exception e)
        {
            _state = State.Idle;
            _recorder = null;
            ShowError("Couldn't start recording", Plain(e));
            return;
        }

        _state = State.Recording;
        HideBar();
        if (t.Kind == TargetKind.Area) _outline.ShowAround(t.Bounds, t.Monitor, interactive: false);
        bool hint = !_s.SeenPillHint;
        if (hint) { _s.SeenPillHint = true; _s.Save(); }
        _pill.Begin(Native.WorkArea(t.Monitor), _s.Sound, hint);
        _ = FinishWhenDone(rec, t);
    }

    public void StopRecording()
    {
        if (_state != State.Recording) return;
        _state = State.Saving;
        _pill.ShowSaving();
        _recorder?.Stop();
    }

    private async Task FinishWhenDone(Recorder rec, CaptureTarget t)
    {
        RecordResult? r = null; Exception? failure = null;
        BitmapSource? thumb = null;
        try
        {
            // The recorder can also end by itself, e.g. the recorded window closed.
            r = await rec.Completed;
        }
        catch (Exception e) { failure = e; }

        _state = State.Saving;
        _recorder = null;
        _outline.HideOutline();
        _pill.End();
        _state = State.Idle;

        if (_exiting) { AfterExitWork?.TrySetResult(); return; }
        ShowBar();

        if (failure != null || r == null)
        {
            ShowError("The recording couldn't be saved", failure != null ? Plain(failure) : "");
            return;
        }

        try { thumb = await Bg.Sta(() => { var g = Snap.Capture(t); return Shrink(g.ToBitmap(), 160); }); }
        catch { /* the thumbnail is a nicety */ }

        try
        {
            Snap.CopyFile(r.Path);
            string body = $"{Clock(r.Seconds)} video · {Fmt.Size(r.FileBytes)}";
            if (r.SoundRequested && !r.SoundCaptured) body += " · no sound device found";
            ShowToast("Copied. Press Ctrl+V to paste.", r.EndedEarlyBecause != null ? $"{r.EndedEarlyBecause} {body}" : body, thumb, r.Path);
        }
        catch (Exception e)
        {
            Log.Write("copy failed: " + e);
            ShowToast("Saved, but the clipboard was busy", "Open the folder to get your video.", thumb, r.Path, ok: false);
        }
    }

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(1, Math.Round(seconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static BitmapSource Shrink(BitmapSource b, int width)
    {
        double f = Math.Min(1, width / (double)b.PixelWidth);
        var t = new TransformedBitmap(b, new ScaleTransform(f, f));
        t.Freeze();
        return t;
    }

    private static string Plain(Exception e) => e switch
    {
        UnauthorizedAccessException => "Windows blocked access to that folder.",
        IOException io when io.Message.Contains("space", StringComparison.OrdinalIgnoreCase) => "Your disk is full.",
        _ => e.Message.Split('\n')[0],
    };

    // ---- Exit ----

    private TaskCompletionSource? AfterExitWork;

    public async void QuitApp()
    {
        if (_exiting) return;
        _exiting = true;
        if (_state is State.Recording or State.Starting or State.Saving)
        {
            AfterExitWork = new TaskCompletionSource();
            _recorder?.Stop();
            await Task.WhenAny(AfterExitWork.Task, Task.Delay(8000));
        }
        CloseDown();
        Application.Current.Shutdown();
    }

    /// <summary>Windows is shutting down: finish the file if there's a recording, briefly and without UI.</summary>
    public void OnSessionEnding()
    {
        _recorder?.Stop();
        try { _recorder?.Completed.Wait(4000); } catch { }
        CloseDown();
    }

    internal void CloseDown()
    {
        _s.Save();
        _tray?.Dispose();
        if (_hwnd != IntPtr.Zero) Native.UnregisterHotKey(_hwnd, HotkeyId);
    }

    // ---- Test support ----

    internal System.Windows.Controls.Grid TestRoot => Root;
    internal void TestSetTarget(CaptureTarget t) { _target = t; SetTargetKind(t.Kind, instant: true); RefreshTargetVisuals(); }
    internal void TestSetMode(bool record) => SetMode(record, instant: true);
    internal void TestShowGuide(bool on) { _guideOpen = on; Guide.Visibility = on ? Visibility.Visible : Visibility.Collapsed; Guide.Opacity = on ? 1 : 0; UpdateStrip(); LayoutStack(); }
    internal void TestToast(string t, string b, BitmapSource? th, bool ok = true) => ShowToast(t, b, th, ok ? "x" : null, ok);
    internal void TestHideToast() { Toast.Visibility = Visibility.Collapsed; Toast.Opacity = 0; }
    internal void TestQuality(string name) { _s.Quality = name; foreach (var c in _cards) c.Chosen = c.Name == name; QualityButton.Word = name; }
    internal void TestStripOpen(bool open) { _s.QualityOpen = open; UpdateStrip(); LayoutStack(); }
    internal void TestSound(bool on) { _s.Sound = on; UpdateSound(); RefreshCards(); }
    internal bool TestShown => _shown;
    internal string TestState => _state.ToString();
    internal PillWindow TestPill => _pill;
    internal IReadOnlyList<CardModel> TestCards => _cards;
    internal IntPtr TestHandle => _hwnd;
    internal void TestClick(string name)
    {
        var e = (System.Windows.Controls.Primitives.ButtonBase)FindName(name);
        e.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, e));
    }
    internal bool TestChecked(string name) => ((System.Windows.Controls.Primitives.ToggleButton)FindName(name)).IsChecked == true;
    internal string? TestWord(string name) => ((ToolButton)FindName(name)).Word;
    internal void TestPickCard(string name)
    {
        var card = _cards.First(c => c.Name == name);
        OnCard(new System.Windows.Controls.Primitives.ToggleButton { Tag = card }, new RoutedEventArgs());
    }
    internal OutlineWindow TestOutline => _outline;
    internal CaptureTarget? TestTarget => _target;
    internal bool TestStripVisible => Strip.Visibility == Visibility.Visible;
    internal string TestGoText => GoText.Text;
    internal Task TestGo(bool record, CaptureTarget t)
    {
        _target = t; SetTargetKind(t.Kind, instant: true); SetMode(record, instant: true);
        return record ? StartRecording(t) : TakeScreenshot(t);
    }
    internal string TestToastText => ToastTitle.Text + " | " + ToastBody.Text;
}
