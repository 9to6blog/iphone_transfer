using System.Windows;
using System.Windows.Threading;

namespace iPhoneTransfer.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _show;
    private RegisteredWaitHandle? _showWait;
    private ConnectionWatcher? _watcher;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] is "--probe" or "--usb-probe")
        {
            ProbeResult result;
            try { result = new(e.Args[0] == "--usb-probe"
                ? iPhoneTransfer.Core.IPhoneClient.ListUsbDeviceIds().Select(id => new iPhoneTransfer.Core.DeviceInfo(id, "iPhone", false)).ToList()
                : iPhoneTransfer.Core.IPhoneClient.ListDevices().ToList()); }
            catch (Exception ex) { result = new([], ex.Message); }
            System.IO.File.WriteAllText(e.Args[1], System.Text.Json.JsonSerializer.Serialize(result));
            Shutdown();
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] is "--ui-check" or "--device-check" or "--connection-check" or "--media-check" or "--app-files-check" or "--app-media-check" or "--video-ranges-check")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                System.IO.Directory.CreateDirectory(e.Args[1]);
                System.IO.File.Delete(System.IO.Path.Combine(e.Args[1], "failure.txt"));
                if (e.Args[0] == "--media-check") await MediaVerification.RunAsync(e.Args[1]);
                else if (e.Args[0] == "--video-ranges-check") await DeviceVerification.RunVideoRangesAsync(e.Args[1]);
                else if (e.Args[0] == "--app-media-check") await DeviceVerification.RunAppMediaAsync(e.Args[1]);
                else if (e.Args[0] == "--app-files-check") await DeviceVerification.RunAppFilesAsync(e.Args[1]);
                else if (e.Args[0] == "--device-check") await DeviceVerification.RunAsync(e.Args[1]);
                else
                {
                    var test = new MainWindow(true) { ShowInTaskbar = false, Left = -3000, Top = -3000, WindowStartupLocation = WindowStartupLocation.Manual };
                    test.Show();
                    if (e.Args[0] == "--connection-check") await test.VerifyConnectionAsync(e.Args[1]);
                    else await test.VerifyUiAsync(e.Args[1]);
                    test.Close();
                }
                Shutdown(0);
            }
            catch (Exception ex)
            {
                System.IO.Directory.CreateDirectory(e.Args[1]);
                System.IO.File.WriteAllText(System.IO.Path.Combine(e.Args[1], "failure.txt"), ex.ToString());
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Contains("--enable-autolaunch")) { AutoLaunch.SetEnabled(true); Shutdown(); return; }
        if (e.Args.Contains("--disable-autolaunch")) { AutoLaunch.SetEnabled(false); Shutdown(); return; }
        var watching = e.Args.Contains("--watch");
        _instance = new Mutex(true, watching ? @"Local\iPhoneTransfer.Watcher" : @"Local\iPhoneTransfer.Window", out var first);
        if (!first)
        {
            if (!watching) AutoLaunch.Signal(AutoLaunch.ShowEvent);
            Shutdown(); return;
        }
        if (watching)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (!AutoLaunch.Enabled) { Shutdown(); return; }
            _watcher = new ConnectionWatcher();
            return;
        }
        var window = new MainWindow();
        if (e.Args.Contains("--connection")) window.OpenConnectionCenter();
        if (e.Args.Contains("--apps")) window.OpenAppFiles();
        MainWindow = window;
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, AutoLaunch.ShowEvent);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }), null, -1, false);
        window.Show();
        if (AutoLaunch.Enabled) AutoLaunch.Start("--watch");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watcher?.Dispose(); _showWait?.Unregister(null); _show?.Dispose(); _instance?.Dispose();
        base.OnExit(e);
    }
    /// <summary>
    /// UI 스레드에서 처리되지 않은 예외를 마지막으로 잡아, 앱이 조용히 죽는 대신
    /// 사용자에게 오류를 보여주고 계속 동작하도록 한다.
    /// </summary>
    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "예기치 못한 오류가 발생했습니다:\n\n" + e.Exception.Message,
            "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
