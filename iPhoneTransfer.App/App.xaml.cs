using System.Windows;
using System.Windows.Threading;

namespace iPhoneTransfer.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--probe")
        {
            ProbeResult result;
            try { result = new(iPhoneTransfer.Core.IPhoneClient.ListDevices().ToList()); }
            catch (Exception ex) { result = new([], ex.Message); }
            System.IO.File.WriteAllText(e.Args[1], System.Text.Json.JsonSerializer.Serialize(result));
            Shutdown();
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] is "--ui-check" or "--device-check" or "--connection-check")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (e.Args[0] == "--device-check") await DeviceVerification.RunAsync(e.Args[1]);
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
        var window = new MainWindow();
        if (e.Args.Contains("--connection")) window.OpenConnectionCenter();
        MainWindow = window;
        window.Show();
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
