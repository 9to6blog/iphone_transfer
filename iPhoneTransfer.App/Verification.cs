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
        BitmapSource appFixture;
        using (var fixture = typeof(MediaVerification).Assembly.GetManifestResourceStream("iPhoneTransfer.TestFixture.heic")!)
        {
            using var buffer = new MemoryStream(); fixture.CopyTo(buffer);
            var preview = MediaPreview.DecodeImage(buffer.ToArray(), 480);
            appFixture = preview;
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
        var appRequests = new List<string>();
        _appMediaLoader = (device, bundle, item, width, token) =>
        {
            token.ThrowIfCancellationRequested();
            appRequests.Add(bundle + ":" + item.DevicePath);
            return Task.FromResult(appFixture);
        };
        Navigate(1);
        AppCombo.ItemsSource = new[] { new SharingApp("fixture.read", "파일 공유 앱") };
        AppCombo.SelectedIndex = 0;
        _appFilesDevice = CurrentDevice!.Udid; _appFilesBundle = "fixture.read";
        _appDirectory = "/Documents/자료"; AppFolderText.Text = _appDirectory;
        _appFiles.Add(new("/Documents/자료/문서", "문서", true, 0, DateTime.Today));
        _appFiles.Add(new("/Documents/자료/book.epub", "book.epub", false, 2_500_000, DateTime.Today));
        _appFiles.Add(new("/Documents/자료/notes.pdf", "notes.pdf", false, 128_000, DateTime.Today));
        var appPhoto = new AppFileRow("/Documents/자료/풍경.HEIC", "풍경.HEIC", false, 718114, DateTime.Today);
        var appVideo = new AppFileRow("/Documents/자료/촬영.MOV", "촬영.MOV", false, 2718114, DateTime.Today);
        _appFiles.Add(appPhoto); _appFiles.Add(appVideo);
        AppFilesEmpty.Visibility = Visibility.Collapsed;
        StartAppThumbnailLoad();
        Check(appPhoto.Thumbnail != null && appVideo.Thumbnail != null && appRequests.Count == 2 && appRequests.All(r => r.StartsWith("fixture.read:/Documents/")),
            "App grid loads photo and video thumbnails from the selected app, excluding folders and documents");
        AppFileList.SelectAll();
        Check(ImportAppFilesBtn.IsEnabled && AppParentBtn.IsEnabled && AppFileList.SelectedItems.Count == 5, "App files support multi-selection and parent navigation");
        AppFileList.UnselectAll(); AppFileList.SelectedItem = appPhoto;
        AppFileList.ScrollIntoView(appPhoto);
        Check(AppPreviewImage.Source != null && AppLargePreviewBtn.IsEnabled && AppFileList.View == null && AppFileList.ItemTemplate != null,
            "App grid is the default and selected HEIC has a side preview and enlarged-view action");
        await CaptureAsync("03-app-import.png");
        Width = 1000; Height = 680;
        await CaptureAsync("07-app-import-compact.png");
        Check(AppFileList.ActualHeight >= 168, $"App import grid fits a full thumbnail row at minimum window size ({AppFileList.ActualHeight:0} px)");
        Width = 1180; Height = 820;
        AppListRadio.IsChecked = true;
        Check(AppFileList.View != null && AppFileList.SelectedItem == appPhoto && AppPreviewImage.Source != null,
            "Switching between grid and list preserves selection and preview");
        await CaptureAsync("08-app-list.png");
        AppGridRadio.IsChecked = true;
        AppFileList.SelectedItem = appVideo;
        Check(AppPreviewCaption.Text.Contains("영상 대표 화면"), "App video preview is clearly labeled as a representative frame");
        var stalePreview = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        _appMediaLoader = (_, _, _, _, _) => stalePreview.Task; // Simulate a native read returning after cancellation.
        appVideo.Thumbnail = null;
        var loadingPreview = ShowAppPreviewAsync(appVideo);
        Cancel_Click(this, new RoutedEventArgs());
        stalePreview.SetResult(appFixture);
        await loadingPreview;
        Check(AppPreviewImage.Source == null && _appPreviewCts == null, "Canceled app preview cannot publish a late image");
        SetBusy(true, "앱 가져오기 검증");
        Check(!ImportAppFilesBtn.IsEnabled && !AppCombo.IsEnabled && !AppParentBtn.IsEnabled && !AppSendRadio.IsEnabled, "App import locks app, direction and navigation until complete");
        SetBusy(false, "준비 완료");
        AppSendRadio.IsChecked = true;
        Check(AppSendPane.Visibility == Visibility.Visible && AppReadPane.Visibility == Visibility.Collapsed, "Existing app upload remains accessible");
        await CaptureAsync("06-app-send.png");
        AppReadRadio.IsChecked = true;
        var oldAppPreview = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        _appMediaLoader = (_, _, _, _, _) => oldAppPreview.Task;
        var oldAppLoad = ShowAppPreviewAsync(appVideo);
        AppCombo.ItemsSource = new[] { new SharingApp("fixture.other", "다른 앱") };
        AppCombo.SelectedIndex = 0;
        oldAppPreview.SetResult(appFixture); await oldAppLoad;
        Check(_appFiles.Count == 0 && _appDirectory == "/Documents" && !ImportAppFilesBtn.IsEnabled && AppPreviewImage.Source == null,
            "Switching apps clears old selections, paths and late previews");
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
            if (MainTabs.SelectedIndex == 1 && AppFileList.SelectedItem != null)
            { AppFileList.ScrollIntoView(AppFileList.SelectedItem); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout(); }
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
    public static async Task RunVideoRangesAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        using var ct = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var device = IPhoneClient.ListDevices().FirstOrDefault(d => d.IsReady) ?? throw new IOException("아이폰 연결이 필요합니다.");
        var reports = new List<object>();
        var photos = await IPhoneClient.ListPhotosAsync(device.Udid, ct.Token);
        var movie = photos.Where(p => MediaPreview.IsVideo(p.FileName)).OrderByDescending(p => p.Size).FirstOrDefault();
        if (movie != null) await Verify(null, movie);
        var apps = await IPhoneClient.ListSharingAppsAsync(device.Udid, ct.Token);
        AppFileItem? best = null; string? bestApp = null;
        foreach (var app in apps)
        {
            try
            {
                var folders = new Queue<(string Path, int Depth)>(); folders.Enqueue(("/Documents", 0));
                for (int i = 0; i < 5 && folders.Count > 0; i++)
                {
                    var folder = folders.Dequeue();
                    var items = await IPhoneClient.ListAppFilesAsync(device.Udid, app.BundleId, folder.Path, ct.Token);
                    var candidate = items.Where(f => !f.IsDirectory && !f.IsSymbolicLink && MediaPreview.IsVideo(f.Name)).OrderByDescending(f => f.Size).FirstOrDefault();
                    if (candidate != null && (best == null || candidate.Size > best.Size)) { best = candidate; bestApp = app.BundleId; }
                    if (folder.Depth < 2) foreach (var child in items.Where(f => f.IsDirectory).Take(4)) folders.Enqueue((child.DevicePath, folder.Depth + 1));
                }
            }
            catch (MobileDeviceException) { }
            if (best?.Size > 50_000_000) break;
        }
        if (best != null) await Verify(bestApp, new(best.DevicePath, best.Name, best.Size, best.Modified));
        await File.WriteAllTextAsync(Path.Combine(directory, "video-ranges-check.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        if (reports.Count == 0) throw new IOException("검증할 영상이 없습니다.");

        async Task Verify(string? app, PhotoItem item)
        {
            Task<BitmapSource> Load(int width) => app == null ? MediaPreview.LoadAsync(device.Udid, item, width, ct.Token)
                : MediaPreview.LoadAppAsync(device.Udid, app, new(item.DevicePath, item.FileName, false, item.Size, item.Modified), width, ct.Token);
            var frame = await Load(480);
            var first = MediaPreview.LastVideoRead!;
            var again = await Load(1920);
            var second = MediaPreview.LastVideoRead!;
            if (first.UsbBytes > 16L * 1024 * 1024 || !second.CacheHit || second.UsbBytes != 0 || !ReferenceEquals(frame, again))
                throw new IOException("영상 부분 읽기/캐시 검증에 실패했습니다.");
            reports.Add(new { source = app == null ? "camera" : "app", first.SourceBytes, first.UsbBytes,
                percentRead = Math.Round(first.UsbBytes * 100.0 / first.SourceBytes, 3), repeatedReadBytes = second.UsbBytes,
                width = frame.PixelWidth, height = frame.PixelHeight, fullMovieCopied = false });
        }
    }

    public static async Task RunAppMediaAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ready = IPhoneClient.ListDevices().FirstOrDefault(d => d.IsReady) ?? throw new IOException("아이폰을 연결하고 잠금을 해제하세요.");
        var apps = await IPhoneClient.ListSharingAppsAsync(ready.Udid, ct.Token);
        var results = new List<object>();
        bool imageDone = false, videoDone = false;
        int directories = 0, rejectedApps = 0;
        foreach (var app in apps)
        {
            try
            {
                var folders = new Queue<(string Path, int Depth)>(); folders.Enqueue(("/Documents", 0));
                for (int count = 0; count < 5 && folders.Count > 0 && !(imageDone && videoDone); count++)
                {
                    var folder = folders.Dequeue();
                    var items = await IPhoneClient.ListAppFilesAsync(ready.Udid, app.BundleId, folder.Path, ct.Token); directories++;
                    foreach (var item in items.Where(i => !i.IsDirectory && !i.IsSymbolicLink && i.Size is > 0 and < 80_000_000).OrderBy(i => i.Size))
                    {
                        var video = MediaPreview.IsVideo(item.Name);
                        if (video ? videoDone : imageDone || !MediaPreview.IsImage(item.Name)) continue;
                        var preview = await MediaPreview.LoadAppAsync(ready.Udid, app.BundleId, item, 480, ct.Token);
                        results.Add(new { kind = video ? "video" : "image", extension = Path.GetExtension(item.Name).ToLowerInvariant(), bytes = item.Size,
                            width = preview.PixelWidth, height = preview.PixelHeight, frozen = preview.IsFrozen });
                        if (video) videoDone = true; else imageDone = true;
                        if (imageDone && videoDone) break;
                    }
                    if (folder.Depth < 2) foreach (var child in items.Where(i => i.IsDirectory).Take(4)) folders.Enqueue((child.DevicePath, folder.Depth + 1));
                }
            }
            catch (MobileDeviceException) { rejectedApps++; }
            if (imageDone && videoDone) break;
        }
        GC.Collect(); GC.WaitForPendingFinalizers();
        await File.WriteAllTextAsync(Path.Combine(directory, "app-media-check.json"), JsonSerializer.Serialize(new
        {
            ready = true, appCount = apps.Count, directories, rejectedApps, previews = results,
            note = "Read-only app document previews. No uploads or device changes; local copies removed. No filenames or device identifiers saved."
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (results.Count == 0) throw new IOException("검색 범위에서 검증할 사진·영상이 없었습니다.");
    }

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
