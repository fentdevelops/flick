using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Flick;

/// <summary>Glass Player's easing, cubic-bezier(.22, .8, .24, 1): quick to start, long soft landing.</summary>
public sealed class GlideEase : EasingFunctionBase
{
    private const double X1 = 0.22, Y1 = 0.8, X2 = 0.24, Y2 = 1;

    public GlideEase() { EasingMode = EasingMode.EaseIn; } // EaseIn = "use EaseInCore as written"

    protected override double EaseInCore(double p)
    {
        if (p <= 0) return 0;
        if (p >= 1) return 1;
        double t = p;
        for (int i = 0; i < 6; i++)
        {
            double x = Bez(t, X1, X2) - p;
            double d = 3 * (1 - t) * (1 - t) * X1 + 6 * (1 - t) * t * (X2 - X1) + 3 * t * t * (1 - X2);
            if (Math.Abs(d) < 1e-5) break;
            t -= x / d;
            t = Math.Clamp(t, 0, 1);
        }
        return Bez(t, Y1, Y2);
    }

    private static double Bez(double t, double a, double b) =>
        3 * (1 - t) * (1 - t) * t * a + 3 * (1 - t) * t * t * b + t * t * t;

    protected override Freezable CreateInstanceCore() => new GlideEase();
}

public static class Anim
{
    private static readonly GlideEase Ease = (GlideEase)new GlideEase().GetAsFrozen();

    /// <summary>Set by the screenshot test so pictures show end states.</summary>
    public static bool Instant { get; set; }

    public static void To(IAnimatable target, DependencyProperty prop, double value, double ms, Action? done = null)
    {
        if (Instant)
        {
            target.BeginAnimation(prop, null);
            if (target is DependencyObject d) d.SetValue(prop, value);
            done?.Invoke();
            return;
        }
        var a = new DoubleAnimation(value, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease };
        if (done != null) a.Completed += (_, _) => done();
        target.BeginAnimation(prop, a);
    }

    /// <summary>Jumps straight to a value. A finished animation keeps holding its end value, so it has to be cleared first.</summary>
    public static void Set(IAnimatable target, DependencyProperty prop, double value)
    {
        target.BeginAnimation(prop, null);
        ((DependencyObject)target).SetValue(prop, value);
    }

    public static void Fade(UIElement e, double value, double ms = 450, Action? done = null) => To(e, UIElement.OpacityProperty, value, ms, done);
}

/// <summary>Icon over a short word.</summary>
public class ToolButton : ToggleButton
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(Geometry), typeof(ToolButton));
    public static readonly DependencyProperty DotIconProperty = DependencyProperty.Register(nameof(DotIcon), typeof(Geometry), typeof(ToolButton));
    public static readonly DependencyProperty WordProperty = DependencyProperty.Register(nameof(Word), typeof(string), typeof(ToolButton));

    public Geometry? Icon { get => (Geometry?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public Geometry? DotIcon { get => (Geometry?)GetValue(DotIconProperty); set => SetValue(DotIconProperty, value); }
    public string? Word { get => (string?)GetValue(WordProperty); set => SetValue(WordProperty, value); }

    static ToolButton() { DefaultStyleKeyProperty.OverrideMetadata(typeof(ToolButton), new FrameworkPropertyMetadata(typeof(ToolButton))); }

    /// <summary>The click handler decides the checked state, so a click alone doesn't flip it.</summary>
    protected override void OnToggle() { }
}

internal static class Win
{
    /// <summary>
    /// Sets a window up as Flick floats: above other apps, hidden from every capture (so it never appears in a recording or
    /// screenshot), and optionally unable to take focus, so using the bar doesn't change which app is active.
    /// </summary>
    public static IntPtr Float(Window w, bool noActivate, bool clickThrough = false)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW;
        if (noActivate) ex |= Native.WS_EX_NOACTIVATE;
        if (clickThrough) ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
        Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, ex);
        if (!Native.SetWindowDisplayAffinity(h, Native.WDA_EXCLUDEFROMCAPTURE))
            Log.Write($"could not hide window from capture, error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        if (noActivate)
        {
            HwndSource.FromHwnd(h)?.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool handled) =>
            {
                if (msg != Native.WM_MOUSEACTIVATE) return IntPtr.Zero;
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
            });
        }
        return h;
    }

    public static void Move(IntPtr h, int x, int y) =>
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    public static void Place(IntPtr h, PxRect r) =>
        Native.SetWindowPos(h, Native.HWND_TOPMOST, r.X, r.Y, r.W, r.H, Native.SWP_NOACTIVATE);

    public static double Scale(IntPtr h)
    {
        uint dpi = Native.GetDpiForWindow(h);
        return dpi > 0 ? dpi / 96.0 : 1;
    }
}

internal static class Bg
{
    /// <summary>Runs work on a short-lived STA thread; WPF imaging objects are happiest there.</summary>
    public static Task<T> Sta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var th = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception e) { tcs.SetException(e); }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        return tcs.Task;
    }
}
