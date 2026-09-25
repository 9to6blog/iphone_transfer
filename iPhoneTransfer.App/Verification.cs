using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using iPhoneTransfer.Core;

namespace iPhoneTransfer.App;

public partial class MainWindow
{
    internal async Task VerifyConnectionAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await RefreshConnectionAsync(true);
        await File.WriteAllTextAsync(Path.Combine(directory, "connection-check.json"), JsonSerializer.Serialize(new
        {
            host = _host, deviceCount = _lastProbe.Devices.Count, ready = CurrentDevice?.IsReady == true,
            error = _lastProbe.Error, driverSummary = DriverCheck.Text,
            probeProcessCompleted = !_refreshing
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (_lastProbe.Error != null || _host?.Error != null) throw new Exception("Connection inspection failed");
    }

    internal async Task VerifyUiAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool success, string message) { if (!success) throw new Exception(message); checks.Add(message); }
        _host = new(19045, "AMD64", false, "NotInstalled", 0, 0);
        RenderConnection();
        Check(DriverCheck.Text.Contains("설치"), "Missing driver has installation guidance");
        Check(UsbCheck.Text.Contains("케이블"), "Missing USB has cable guidance");
        await CaptureAsync("01-photos.png");
        Navigate(2);
        await CaptureAsync("02-connection.png");
        Check(AutoLaunchCheck.Content.ToString()!.Contains("자동 실행"), "Connection center exposes automatic launch preference");
        _host = new(26200, "AMD64", true, "NotInstalled", 2, 0);
        DeviceCombo.ItemsSource = new[] { new DeviceInfo("fixture-one", "테스트 iPhone", false, "아이폰 잠금을 해제하고 신뢰를 승인하세요.") };
        DeviceCombo.SelectedIndex = 0;
        RenderConnection();
        Check(ConnectionTitle.Text.Contains("확인"), "Untrusted device is not reported ready");
        Check(!RequireDevice(), "Untrusted device cannot transfer");
        DeviceCombo.ItemsSource = new[] { new DeviceInfo("fixture-one", "테스트 iPhone") };
        DeviceCombo.SelectedIndex = 0;
        RenderConnection();
        Check(RequireDevice(), "Ready device can transfer");
        using (var fixture = typeof(MediaVerification).Assembly.GetManifestResourceStream("iPhoneTransfer.TestFixture.heic")!)
        {
            using var buffer = new MemoryStream(); fixture.CopyTo(buffer);
            var preview = MediaPreview.DecodeImage(buffer.ToArray(), 480);
            _photos.Add(new(new PhotoItem("/DCIM/IMG_0001.HEIC", "IMG_0001.HEIC", 718114, DateTime.Today)) { Thumbnail = preview });
            _photos.Add(new(new PhotoItem("/DCIM/IMG_0001.MOV", "IMG_0001.MOV", 2718114, DateTime.Today)) { Thumbnail = preview });
            _photos.Add(new(new PhotoItem("/DCIM/error.HEIC", "error.HEIC", 100, DateTime.Today)) { PreviewError = "테스트: 원본을 다시 읽어 주세요." });
            PreviewImage.Source = preview; PreviewMsg.Visibility = Visibility.Collapsed;
            PreviewCaption.Text = "IMG_0001.HEIC · HEIC 미리보기";
            Navigate(0);
            await CaptureAsync("05-media-preview.png");
            Check(_photos[1].MediaLabel == "영상" && _photos[2].Placeholder.Contains("재시도"), "Video badge and retry guidance remain visible");
        }
        Check(DriverCheck.Text.Contains("설치되어"), "Store Apple Devices does not require desktop service");
        _photos.Add(new(new PhotoItem("/DCIM/test.jpg", "test.jpg", 100, DateTime.Today)));
        AppCombo.ItemsSource = new[] { new SharingApp("fixture", "Fixture") };
        _appFiles.Add(new("/Documents/stale.pdf", "stale.pdf", false, 50, DateTime.Today));
        DeviceCombo.ItemsSource = new[] { new DeviceInfo("fixture-two", "두 번째 테스트 iPhone") };
        DeviceCombo.SelectedIndex = 0;
        Check(_photos.Count == 0 && AppCombo.ItemsSource == null && _appFiles.Count == 0, "Switching devices clears stale files and apps");
        SetBusy(true, "테스트");
        Check(!DeviceCombo.IsEnabled && !RepairBtn.IsEnabled && !FilesToSend.AllowDrop, "Transfer locks device, repair and drop controls");
        SetBusy(false, "준비 완료");
        Navigate(1);
        AppCombo.ItemsSource = new[] { new SharingApp("fixture.read", "파일 공유 앱") };
        AppCombo.SelectedIndex = 0;
        _appFilesDevice = CurrentDevice!.Udid; _appFilesBundle = "fixture.read";
        _appDirectory = "/Documents/자료"; AppFolderText.Text = _appDirectory;
        _appFiles.Add(new("/Documents/자료/문서", "문서", true, 0, DateTime.Today));
        _appFiles.Add(new("/Documents/자료/book.epub", "book.epub", false, 2_500_000, DateTime.Today));
        _appFiles.Add(new("/Documents/자료/notes.pdf", "notes.pdf", false, 128_000, DateTime.Today));
        AppFilesEmpty.Visibility = Visibility.Collapsed;
        AppFileList.SelectAll();
        Check(ImportAppFilesBtn.IsEnabled && AppParentBtn.IsEnabled && AppFileList.SelectedItems.Count == 3, "App files support multi-selection and parent navigation");
        await CaptureAsync("03-app-import.png");
        Width = 1000; Height = 680;
        await CaptureAsync("07-app-import-compact.png");
        Check(AppFileList.ActualHeight >= 65, "App import list remains usable at minimum window size");
        Width = 1180; Height = 820;
        SetBusy(true, "앱 가져오기 검증");
        Check(!ImportAppFilesBtn.IsEnabled && !AppCombo.IsEnabled && !AppParentBtn.IsEnabled && !AppSendRadio.IsEnabled, "App import locks app, direction and navigation until complete");
        SetBusy(false, "준비 완료");
        AppSendRadio.IsChecked = true;
        Check(AppSendPane.Visibility == Visibility.Visible && AppReadPane.Visibility == Visibility.Collapsed, "Existing app upload remains accessible");
        await CaptureAsync("06-app-send.png");
        AppReadRadio.IsChecked = true;
        AppCombo.ItemsSource = new[] { new SharingApp("fixture.other", "다른 앱") };
        AppCombo.SelectedIndex = 0;
        Check(_appFiles.Count == 0 && _appDirectory == "/Documents" && !ImportAppFilesBtn.IsEnabled, "Switching apps clears old app selections and paths");
        Navigate(2);
        Width = 1000; Height = 680;
        await CaptureAsync("04-compact.png");
        DeviceCombo.ItemsSource = Array.Empty<DeviceInfo>();
        _host = new(26200, "AMD64", true, "NotInstalled", 0, 0);
        RenderConnection();
        Check(ConnectionTitle.Text.Contains("기다리고"), "Disconnect resets connection readiness");
        await File.WriteAllTextAsync(Path.Combine(directory, "ui-checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));

        async Task CaptureAsync(string name)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            UpdateLayout();
            var content = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
        }
    }
}

internal static class DeviceVerification
{
    public static async Task RunAppFilesAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ready = IPhoneClient.ListDevices().FirstOrDefault(d => d.IsReady);
        if (ready == null) throw new IOException("검증할 아이폰이 준비되지 않았습니다. 잠금을 해제하고 신뢰를 승인하세요.");
        var apps = await IPhoneClient.ListSharingAppsAsync(ready.Udid, ct.Token);
        int checkedApps = 0, checkedDirectories = 0, rejectedApps = 0;
        AppFileItem? sample = null; SharingApp? sampleApp = null;
        foreach (var app in apps)
        {
            try
            {
                var items = await IPhoneClient.ListAppFilesAsync(ready.Udid, app.BundleId, ct: ct.Token);
                checkedApps++; checkedDirectories++;
                sample = items.Where(i => !i.IsDirectory && !i.IsSymbolicLink && i.Size is > 0 and < 1_000_000).OrderBy(i => i.Size).FirstOrDefault();
                if (sample == null)
                    foreach (var folder in items.Where(i => i.IsDirectory).Take(3))
                    {
                        var nested = await IPhoneClient.ListAppFilesAsync(ready.Udid, app.BundleId, folder.DevicePath, ct.Token);
                        checkedDirectories++;
                        sample = nested.Where(i => !i.IsDirectory && !i.IsSymbolicLink && i.Size is > 0 and < 1_000_000).OrderBy(i => i.Size).FirstOrDefault();
                        if (sample != null) break;
                    }
                if (sample != null) { sampleApp = app; break; }
            }
            catch (MobileDeviceException) { rejectedApps++; }
        }
        bool? matches = null;
        if (sample != null && sampleApp != null)
        {
            // Keep the verification local. Only hashes/counts reach the report; no names or device IDs.
            var temporary = Path.Combine(Path.GetTempPath(), "iPhoneTransfer-app-check-" + Guid.NewGuid().ToString("N"));
            try
            {
                var first = Path.Combine(temporary, "first"); var second = Path.Combine(temporary, "second");
                await IPhoneClient.ImportAppFilesAsync(ready.Udid, sampleApp.BundleId, new[] { sample.DevicePath }, first, ct: ct.Token);
                await IPhoneClient.ImportAppFilesAsync(ready.Udid, sampleApp.BundleId, new[] { sample.DevicePath }, second, ct: ct.Token);
                var firstFile = Directory.GetFiles(first).Single(); var secondFile = Directory.GetFiles(second).Single();
                using var a = File.OpenRead(firstFile); using var b = File.OpenRead(secondFile);
                matches = a.Length == sample.Size && b.Length == sample.Size && SHA256.HashData(a).SequenceEqual(SHA256.HashData(b));
            }
            finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        await File.WriteAllTextAsync(Path.Combine(directory, "app-files-check.json"), JsonSerializer.Serialize(new
        {
            ready = true, appCount = apps.Count, checkedApps, checkedDirectories, rejectedApps,
            sampleImportBytes = sample?.Size, repeatedImportSha256Matches = matches,
            note = "Read-only app browsing and two local copies of one small file. Samples removed; iPhone files unchanged."
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (matches == false || checkedApps == 0) throw new IOException("앱 파일 검증에 실패했습니다.");
    }

    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        void Stage(string value) => File.AppendAllText(Path.Combine(directory, "stages.txt"), value + "\n");
        Stage("Discover");
        var devices = IPhoneClient.ListDevices();
        var ready = devices.FirstOrDefault(d => d.IsReady);
        if (ready == null)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "device-check.json"), JsonSerializer.Serialize(new { deviceCount = devices.Count, ready = false, errors = devices.Select(d => d.ConnectionError).ToArray() }));
            return;
        }
        Stage("List photos");
        var photos = await IPhoneClient.ListPhotosAsync(ready.Udid, ct.Token);
        Stage("List sharing apps");
        var apps = await IPhoneClient.ListSharingAppsAsync(ready.Udid, ct.Token);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Stage("App handles finalized safely");
        var sample = photos.LastOrDefault(p => p.Size is > 0 and < 5_000_000);
        Stage("Read sample");
        bool? samplePassed = null;
        if (sample != null)
        {
            var expected = await IPhoneClient.ReadFileBytesAsync(ready.Udid, sample.DevicePath, 0, ct.Token);
            var sampleDir = Path.Combine(directory, "private-transfer-sample");
            Stage("Import sample");
            await IPhoneClient.ImportPhotosAsync(ready.Udid, new[] { sample }, sampleDir, ct: ct.Token);
            var actual = await File.ReadAllBytesAsync(Path.Combine(sampleDir, sample.FileName), ct.Token);
            samplePassed = expected.LongLength == sample.Size && SHA256.HashData(expected).SequenceEqual(SHA256.HashData(actual));
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "device-check.json"), JsonSerializer.Serialize(new
        {
            deviceCount = devices.Count, ready = true, photoCount = photos.Count, sharingAppCount = apps.Count,
            sampleImportSha256Matches = samplePassed, sampleBytes = sample?.Size,
            note = "Read-only discovery and one original import. No phone writes, deletions, driver changes or identifiers in this report."
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (samplePassed == false) throw new IOException("Imported sample checksum mismatch");
    }
}
