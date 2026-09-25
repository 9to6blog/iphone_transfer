using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace iPhoneTransfer.App;

internal static class AutoLaunch
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "iPhoneTransfer";
    internal const string StopEvent = @"Local\iPhoneTransfer.Watcher.Stop";
    internal const string ShowEvent = @"Local\iPhoneTransfer.Window.Show";

    internal static bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(ValueName) is string command && command.EndsWith(" --watch", StringComparison.Ordinal); }
    }

    internal static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var command = $"\"{Environment.ProcessPath}\" --watch";
            if (command.Length > 260) throw new IOException("설치 경로가 너무 깁니다. 더 짧은 경로에 설치해 주세요.");
            key.SetValue(ValueName, command, RegistryValueKind.String);
            Start("--watch");
        }
        else
        {
            key.DeleteValue(ValueName, false);
            Signal(StopEvent);
        }
    }

    internal static void Start(string argument)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
    }

    internal static void Signal(string name)
    {
        try { using var signal = EventWaitHandle.OpenExisting(name); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
    }
}

/// <summary>Only opens the app on USB arrival. No pairing prompts, photo scans or file transfers in the watcher.</summary>
internal sealed class ConnectionWatcher : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Forms.NotifyIcon _tray;
    private readonly EventWaitHandle _stop = new(false, EventResetMode.AutoReset, AutoLaunch.StopEvent);
    private readonly RegisteredWaitHandle _stopWait;
    private readonly UsbArrivalTracker _arrivals = new();
    private bool _checking;

    internal ConnectionWatcher()
    {
        var app = Application.Current;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("iPhoneTransfer 열기", null, (_, _) => AutoLaunch.Start("--show"));
        menu.Items.Add("자동 실행 끄기", null, (_, _) => { AutoLaunch.SetEnabled(false); app.Shutdown(); });
        menu.Items.Add("연결 감지 종료 (다음 로그인까지)", null, (_, _) => app.Shutdown());
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "iPhoneTransfer · 아이폰 연결 대기", ContextMenuStrip = menu, Visible = true
        };
        _tray.DoubleClick += (_, _) => AutoLaunch.Start("--show");
        _stopWait = ThreadPool.RegisterWaitForSingleObject(_stop, (_, _) => app.Dispatcher.BeginInvoke(() => app.Shutdown()), null, -1, true);
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
        _ = CheckAsync();
    }

    private async Task CheckAsync()
    {
        if (_checking || _lifetime.IsCancellationRequested) return;
        _checking = true;
        try
        {
            var result = await ConnectionSupport.ProbeAsync(_lifetime.Token, usbOnly: true);
            // A timeout/driver error is not a disconnect; preserve the arrival history.
            if (result.Error != null || _lifetime.IsCancellationRequested) return;
            if (_arrivals.Update(result)) AutoLaunch.Start("--device-arrived");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ConnectionSupport.Log("Connection watcher: " + ex.GetType().Name); }
        finally { _checking = false; }
    }

    public void Dispose()
    {
        _timer.Stop(); _lifetime.Cancel(); _stopWait.Unregister(null); _stop.Dispose();
        _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Icon?.Dispose(); _tray.Dispose();
    }
}

internal sealed class UsbArrivalTracker
{
    private HashSet<string> _present = new(StringComparer.Ordinal);
    internal bool Update(ProbeResult result)
    {
        if (result.Error != null) return false;
        var current = result.Devices.Select(d => d.Udid).ToHashSet(StringComparer.Ordinal);
        var arrived = current.Except(_present).Any();
        _present = current;
        return arrived;
    }
}
