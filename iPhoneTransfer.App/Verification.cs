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
        Check(DriverCheck.Text.Contains("설치되어"), "Store Apple Devices does not require desktop service");
        _photos.Add(new(new PhotoItem("/DCIM/test.jpg", "test.jpg", 100, DateTime.Today)));
        AppCombo.ItemsSource = new[] { new SharingApp("fixture", "Fixture") };
        DeviceCombo.ItemsSource = new[] { new DeviceInfo("fixture-two", "두 번째 테스트 iPhone") };
        DeviceCombo.SelectedIndex = 0;
        Check(_photos.Count == 0 && AppCombo.ItemsSource == null, "Switching devices clears stale files and apps");
        SetBusy(true, "테스트");
        Check(!DeviceCombo.IsEnabled && !RepairBtn.IsEnabled && !FilesToSend.AllowDrop, "Transfer locks device, repair and drop controls");
        SetBusy(false, "준비 완료");
        Navigate(1);
        await CaptureAsync("03-send.png");
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
