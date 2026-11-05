using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Flick;

public partial class App : Application
{
    private Mutex? _single;
    private EventWaitHandle? _showSignal;
    private BarWindow? _bar;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            // One bad moment in the UI shouldn't take a recording down with it.
            Log.Write("ui error: " + ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("fatal: " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Write("task error: " + ex.Exception); ex.SetObserved(); };

        if (e.Args.Length >= 2 && e.Args[0].StartsWith("--"))
        {
            int code;
            try
            {
                code = e.Args[0] switch
                {
                    "--selftest" => await SelfTest.Run(e.Args[1]),
                    "--ui-shots" => await UiShots.Run(e.Args[1]),
                    "--flow-test" => await FlowTest.Run(e.Args[1]),
                    _ => 64,
                };
            }
            catch (Exception ex)
            {
                File.AppendAllText(Path.Combine(e.Args[1], "crash.txt"), ex.ToString());
                code = 2;
            }
            Shutdown(code);
            return;
        }

        // Only one Flick at a time. Opening it again just brings the bar back.
        _single = new Mutex(true, @"Local\Flick.Single", out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Flick.Show");
        if (!first)
        {
            _showSignal.Set();
            Shutdown(0);
            return;
        }
        var signal = _showSignal;
        var waiter = new Thread(() =>
        {
            while (true)
            {
                signal.WaitOne();
                Dispatcher.BeginInvoke(new Action(() => _bar?.ShowBar()));
            }
        }) { IsBackground = true };
        waiter.Start();

        _bar = new BarWindow();
        _bar.ShowFirstTime();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _bar?.OnSessionEnding();
        base.OnSessionEnding(e);
    }
}
