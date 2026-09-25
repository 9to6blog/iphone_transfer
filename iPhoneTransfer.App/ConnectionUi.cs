using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using iPhoneTransfer.Core;
using Microsoft.Win32;

namespace iPhoneTransfer.App;

public partial class MainWindow
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _connectionTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private bool _refreshing, _actionRunning, _updatingSelection;
    private HostStatus? _host;
    private ProbeResult _lastProbe = new([]);
    private DateTime _hostChecked;
    private string? _selectedUdid;

    private void InitializeConnectionUi(bool testMode)
    {
        Navigate(0);
        Loaded += async (_, _) =>
        {
            if (testMode) return;
            Height = Math.Min(Height, SystemParameters.WorkArea.Height - 28);
            await RefreshConnectionAsync(true);
            _connectionTimer.Start();
        };
        _connectionTimer.Tick += async (_, _) => await RefreshConnectionAsync(false);
        Closing += (_, e) =>
        {
            if (_actionRunning) { e.Cancel = true; ActionResult.Text = "설치 또는 복구가 진행 중입니다. 완료될 때까지 기다려 주세요."; Navigate(2); return; }
            if (_busy) { e.Cancel = true; _opCts?.Cancel(); StatusText.Text = "작업을 취소 중입니다. 정리가 끝난 후 창을 닫아 주세요."; return; }
            _connectionTimer.Stop(); _lifetime.Cancel(); _thumbCts?.Cancel(); _previewCts?.Cancel();
        };
    }

    private void Navigate_Click(object sender, RoutedEventArgs e) => Navigate(int.Parse((string)((Button)sender).Tag));
    internal void OpenConnectionCenter() => Navigate(2);
    private void Navigate(int index)
    {
        MainTabs.SelectedIndex = index;
        PageTitle.Text = new[] { "사진 가져오기", "앱으로 보내기", "연결 센터" }[index];
        PageSubtitle.Text = new[] { "아이폰에 담긴 순간을, PC에서도 오래도록.", "파일 공유 앱으로 원하는 파일을 전송하세요.", "연결부터 드라이버 설치까지, 차근차근." }[index];
        var buttons = new[] { PhotosNav, SendNav, ConnectionNav };
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].Background = (Brush)new BrushConverter().ConvertFromString(i == index ? "#2864EA" : "Transparent")!;
            buttons[i].Foreground = (Brush)new BrushConverter().ConvertFromString(i == index ? "White" : "#BFCEE3")!;
        }
    }

    private async Task RefreshConnectionAsync(bool forceHost)
    {
        if (_refreshing || _busy || _actionRunning || _lifetime.IsCancellationRequested) return;
        _refreshing = true;
        RefreshDevicesBtn.IsEnabled = false;
        DiagnoseBtn.IsEnabled = false;
        try
        {
            var probeTask = ConnectionSupport.ProbeAsync(_lifetime.Token);
            if (forceHost || _host == null || DateTime.UtcNow - _hostChecked > TimeSpan.FromSeconds(45))
            {
                _host = await ConnectionSupport.InspectAsync(_lifetime.Token);
                _hostChecked = DateTime.UtcNow;
            }
            var probe = await probeTask;
            if (_busy || _actionRunning || _lifetime.IsCancellationRequested) return;
            _lastProbe = probe;
            var udid = CurrentDevice?.Udid;
            _updatingSelection = true;
            DeviceCombo.ItemsSource = probe.Devices;
            DeviceCombo.SelectedItem = probe.Devices.FirstOrDefault(d => d.Udid == udid)
                ?? probe.Devices.FirstOrDefault(d => d.IsReady) ?? probe.Devices.FirstOrDefault();
            _updatingSelection = false;
            ApplySelectedDevice();
            RenderConnection();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _lastProbe = new([], ex.Message);
            RenderConnection();
            ConnectionSupport.Log("Probe: " + ex.Message);
        }
        finally
        {
            _refreshing = false; _updatingSelection = false;
            RefreshDevicesBtn.IsEnabled = !_busy;
            DiagnoseBtn.IsEnabled = !_busy;
        }
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection || MainTabs == null) return;
        ApplySelectedDevice();
        RenderConnection();
    }

    private void ApplySelectedDevice()
    {
        if (_selectedUdid == CurrentDevice?.Udid && CurrentDevice?.IsReady == true) return;
        _selectedUdid = CurrentDevice?.Udid;
        _thumbCts?.Cancel(); _previewCts?.Cancel();
        _photos.Clear(); _photoSummary = ""; _anchor = -1;
        AppCombo.ItemsSource = null;
        PreviewImage.Source = null; PreviewCaption.Text = "";
        PreviewMsg.Text = "선택한 사진의 미리보기가 표시됩니다.";
        PreviewMsg.Visibility = Visibility.Visible;
        UpdateSelectionCount();
    }

    private void RenderConnection()
    {
        var host = _host;
        WindowsCheck.Text = host == null ? "Windows 확인 중…" :
            $"Windows {(host.Build >= 22000 ? "11" : "10")} · 빌드 {host.Build} · {host.Architecture}" +
            (host.Build < 19045 ? " — Apple Devices 사용을 위해 Windows 업데이트를 권장합니다." : "");
        DriverCheck.Text = host == null ? "드라이버 확인 중…" : host.Error != null ? "진단 일부를 확인하지 못했습니다: " + host.Error :
            host.AppleDevices ? "Apple Devices가 설치되어 있습니다. USB 상태와 기기 접근 결과를 함께 확인하세요." :
            host.Service == "Running" ? "Apple Mobile Device Service가 실행 중입니다." :
            host.Service == "NotInstalled" ? "Apple 기기 지원을 찾지 못했습니다. Apple Devices를 설치하세요." :
            $"Apple 서비스 상태: {host.Service}. ‘드라이버 · 서비스 복구’를 실행하세요.";
        UsbCheck.Text = host == null ? "USB 확인 중…" : host.Error != null ? "Windows 진단이 완전하지 않습니다. ‘다시 진단’을 누르세요." :
            host.UsbErrors > 0 ? $"Apple USB 장치 {host.UsbErrors}개에 오류가 있습니다. 복구 후 케이블을 다시 연결하세요." :
            host.UsbCount > 0 ? "Apple USB 장치가 정상 인식됩니다." :
            "Apple USB 장치가 보이지 않습니다. 데이터 전송 케이블과 다른 USB 포트를 확인하세요.";
        var device = CurrentDevice;
        if (device?.IsReady == true)
        {
            ConnectionTitle.Text = device.Name + " · 연결 준비 완료";
            ConnectionDetail.Text = "기기 신뢰 승인과 파일 서비스 접근을 확인했습니다. 전송을 시작하세요.";
            TrustCheck.Text = "신뢰 승인 및 파일 서비스 접근 확인 완료";
        }
        else if (device != null)
        {
            ConnectionTitle.Text = "아이폰에서 확인이 필요합니다";
            ConnectionDetail.Text = device.ConnectionError ?? "잠금 해제 후 ‘이 컴퓨터를 신뢰’를 선택하세요.";
            TrustCheck.Text = ConnectionDetail.Text;
        }
        else
        {
            ConnectionTitle.Text = _lastProbe.Error != null ? "연결을 확인해 주세요" : "아이폰 연결을 기다리고 있습니다";
            ConnectionDetail.Text = _lastProbe.Error ?? "데이터 전송 케이블로 연결하고 잠금을 해제하세요. 8초마다 자동으로 확인합니다.";
            TrustCheck.Text = "아이폰이 검색되면 신뢰 승인과 파일 접근을 검사합니다.";
        }
    }

    private async void Diagnose_Click(object sender, RoutedEventArgs e) => await RefreshConnectionAsync(true);
    private async void InstallApple_Click(object sender, RoutedEventArgs e) => await RunSetupAsync("InstallAppleDevices");
    private async void InstallITunes_Click(object sender, RoutedEventArgs e) => await RunSetupAsync("InstallITunes");
    private async void Repair_Click(object sender, RoutedEventArgs e) => await RunSetupAsync("Repair");

    private async Task RunSetupAsync(string action)
    {
        if (_busy || _actionRunning) return;
        _actionRunning = true;
        Navigate(2);
        SetBusy(true, "설치 또는 복구 진행 중…");
        CancelBtn.IsEnabled = false;
        ActionResult.Text = "설치 또는 복구 중입니다. Windows 승인 창이 나타나면 승인해 주세요. 설치에는 몇 분이 걸릴 수 있습니다.";
        try
        {
            while (_refreshing) await Task.Delay(100);
            _thumbCts?.Cancel(); _previewCts?.Cancel();
            var code = await ConnectionSupport.RunActionAsync(action);
            ActionResult.Text = code switch
            {
                0 => "설치/복구 명령이 완료되었습니다. 실제 연결 상태를 다시 확인합니다.",
                10 => "Microsoft Store를 열었습니다. Apple Devices의 설치/업데이트를 완료해 주세요. 설치 완료 여부는 자동으로 다시 확인합니다.",
                11 => "설치 명령은 끝났지만 Apple Devices가 확인되지 않았습니다. Store 설치 상태를 확인해 주세요.",
                12 => "Apple Devices가 이미 설치되어 있습니다. 중복 설치 대신 기존 Apple Devices 업데이트/복구를 먼저 진행하세요.",
                13 => "USB를 다시 검색했습니다. 기존 데스크톱 Apple 드라이버는 발견되지 않았습니다. Apple Devices 설치/업데이트를 확인하세요.",
                3010 or 1641 => "설치 마무리에 Windows 재시작이 필요합니다. 작업을 저장한 뒤 직접 재시작해 주세요.",
                20 or 1602 or 1223 => "설치 또는 관리자 승인이 취소되었습니다. 필요할 때 다시 실행할 수 있습니다.",
                21 => "관리자 권한이 필요합니다. Windows 승인 창에서 허용한 뒤 다시 시도하세요.",
                _ => $"설치/복구가 완료되지 않았습니다 (코드 {code}). 인터넷 연결과 관리자 권한을 확인하고 진단 보고서를 저장해 주세요."
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { ActionResult.Text = "관리자 승인이 취소되었습니다. 변경하지 않았습니다."; }
        catch (Exception ex) { ActionResult.Text = "설치/복구 실행 실패: " + ex.Message; ConnectionSupport.Log(ActionResult.Text); }
        finally { _actionRunning = false; SetBusy(false, "연결 상태 재확인 중…"); await RefreshConnectionAsync(true); }
    }

    private void WindowsUpdate_Click(object sender, RoutedEventArgs e) => OpenUrl("ms-settings:windowsupdate", "https://support.microsoft.com/windows");
    private void DeviceManager_Click(object sender, RoutedEventArgs e) => OpenUrl("devmgmt.msc", "https://support.apple.com/108643");
    private void AppleHelp_Click(object sender, RoutedEventArgs e) => OpenUrl("https://support.apple.com/ko-kr/108643", "https://support.apple.com/108643");
    private void SaveDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = $"iPhoneTransfer-진단-{DateTime.Now:yyyyMMdd-HHmm}.txt", Filter = "진단 보고서|*.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            // Device identifiers and filenames are intentionally excluded from the exported report.
            File.WriteAllText(dialog.FileName, $"iPhoneTransfer 2.0 | {DateTime.Now:O}\n{WindowsCheck.Text}\n{DriverCheck.Text}\n{UsbCheck.Text}\n{TrustCheck.Text}\n기기 수: {_lastProbe.Devices.Count}\n{ActionResult.Text}\n{_lastProbe.Error}\n");
            ActionResult.Text = "진단 보고서를 저장했습니다. 기기 식별번호와 사진 이름은 포함하지 않습니다.";
        }
        catch (Exception ex) { ActionResult.Text = "보고서 저장 실패: " + ex.Message; }
    }
}
