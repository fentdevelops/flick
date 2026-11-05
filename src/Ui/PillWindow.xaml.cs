using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Flick;

/// <summary>
/// The only thing on screen while recording: a timer and a stop button. Hidden from every capture, so it's never in the video.
/// It stays visible to the user but quiets down after a few seconds so it isn't distracting.
/// </summary>
public partial class PillWindow : Window
{
    public event Action? StopRequested;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _quiet = new() { Interval = TimeSpan.FromSeconds(3) };
    private DateTime _start;
    private IntPtr _hwnd;
    private bool _dragging;
    private Native.POINT _dragOffset;
    private bool _saving;

    public IntPtr Handle => _hwnd;

    public PillWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => _hwnd = Win.Float(this, noActivate: true);
        _clock.Tick += (_, _) => Clock.Text = Time(DateTime.UtcNow - _start);
        _quiet.Tick += (_, _) => { _quiet.Stop(); if (!_saving && !Root.IsMouseOver) Anim.Fade(Root, 0.5, 900); };
    }

    private static string Time(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    public void Begin(PxRect monitorWork, bool sound, bool showHint)
    {
        _saving = false;
        Clock.Text = "0:00";
        StopButton.IsEnabled = true;
        StopButton.Visibility = Visibility.Visible;
        SoundIcon.Data = (Geometry)FindResource(sound ? "IconSoundOn" : "IconSoundOff");
        SoundIcon.Visibility = Visibility.Visible;
        Show();
        double s = Win.Scale(_hwnd);
        Win.Move(_hwnd, monitorWork.X + (monitorWork.W - (int)(Width * s)) / 2, monitorWork.Y + (int)(4 * s));
        _start = DateTime.UtcNow;
        _clock.Start();
        Anim.Fade(Root, 1, 450);
        Anim.To(Slide, TranslateTransform.YProperty, 0, 600);
        if (showHint)
        {
            Anim.Fade(Hint, 1, 500);
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            t.Tick += (_, _) => { t.Stop(); Anim.Fade(Hint, 0, 700); };
            t.Start();
            _quiet.Interval = TimeSpan.FromSeconds(6);
        }
        else { Anim.Set(Hint, OpacityProperty, 0); _quiet.Interval = TimeSpan.FromSeconds(3); }
        _quiet.Start();
    }

    public void ShowSaving()
    {
        _saving = true;
        _clock.Stop(); _quiet.Stop();
        Clock.Text = "Saving";
        StopButton.IsEnabled = false;
        StopButton.Visibility = Visibility.Collapsed;
        SoundIcon.Visibility = Visibility.Collapsed;
        Anim.Fade(Root, 1, 200);
    }

    public void End()
    {
        _clock.Stop(); _quiet.Stop();
        Anim.To(Slide, TranslateTransform.YProperty, -10, 350);
        Anim.Fade(Root, 0, 300, () =>
        {
            if (Root.Opacity < 0.01) Hide();
        });
    }

    private void OnEnter(object s, MouseEventArgs e) { _quiet.Stop(); Anim.Fade(Root, 1, 300); }
    private void OnLeave(object s, MouseEventArgs e) { if (!_saving) { _quiet.Interval = TimeSpan.FromSeconds(3); _quiet.Start(); } }
    private void OnStop(object s, RoutedEventArgs e) => StopRequested?.Invoke();

    private void OnDragStart(object s, MouseButtonEventArgs e)
    {
        Native.GetCursorPos(out var c);
        Native.GetWindowRect(_hwnd, out var r);
        _dragOffset = new Native.POINT { X = c.X - r.Left, Y = c.Y - r.Top };
        _dragging = true;
        Body.CaptureMouse();
    }

    private void OnDragMove(object s, MouseEventArgs e)
    {
        if (!_dragging) return;
        Native.GetCursorPos(out var c);
        Win.Move(_hwnd, c.X - _dragOffset.X, c.Y - _dragOffset.Y);
    }

    private void OnDragEnd(object s, MouseButtonEventArgs e) { _dragging = false; Body.ReleaseMouseCapture(); }
}
