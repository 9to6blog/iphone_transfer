using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using iPhoneTransfer.Core;
using Microsoft.Win32;

namespace iPhoneTransfer.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PhotoRow> _photos = new();
    private readonly ObservableCollection<string> _filesToSend = new();
    private string? _importFolder;
    private bool _busy;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _thumbCts;
    private CancellationTokenSource? _opCts;     // 가져오기/보내기 등 취소 가능한 작업
    private int _anchor = -1;            // Shift 범위 선택 기준점
    private string _photoSummary = "";   // "총 N개 ..." 요약(선택 개수와 합쳐 표시)


    public MainWindow(bool testMode = false)
    {
        InitializeComponent();
        PhotoList.ItemsSource = _photos;
        PhotoGrid.ItemsSource = _photos;
        FilesToSend.ItemsSource = _filesToSend;
        AppFileList.ItemsSource = _appFiles;
        _filesToSend.CollectionChanged += (_, _) =>
            DropHint.Visibility = _filesToSend.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewGridRadio.IsChecked = true;   // 그리드(썸네일)를 기본 보기로
        _photos.CollectionChanged += (_, _) => PhotoEmpty.Visibility = _photos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InitializeConnectionUi(testMode);
        AutoLaunchCheck.IsChecked = AutoLaunch.Enabled;
        UpdateAppBrowserButtons();
    }

    // ───────────── 공용 헬퍼 ─────────────

    private DeviceInfo? CurrentDevice => DeviceCombo.SelectedItem as DeviceInfo;

    private bool RequireDevice()
    {
        if (CurrentDevice?.IsReady == true) return true;
        Navigate(2);
        ActionResult.Text = "아이폰 연결과 신뢰 승인이 필요합니다. 아래 진단 결과를 확인하세요.";
        return false;
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        StatusText.Text = status;
        if (busy) Progress.Value = 0;
        Progress.IsIndeterminate = busy;
        foreach (var b in new Button[]
        {
            RefreshDevicesBtn, LoadPhotosBtn, SelectAllBtn, UnselectAllBtn, ImportBtn,
            LoadAppsBtn, AddFilesBtn, ClearFilesBtn, SendBtn, DiagnoseBtn, InstallAppleBtn, RepairBtn, InstallITunesBtn
        })
            b.IsEnabled = !busy;
        UpdateCancelButton();
        DeviceCombo.IsEnabled = !busy;
        AppCombo.IsEnabled = !busy;
        ConvertJpegCheck.IsEnabled = !busy;
        FilesToSend.AllowDrop = !busy;
        AppReadRadio.IsEnabled = AppSendRadio.IsEnabled = !busy;
        AppGridRadio.IsEnabled = AppListRadio.IsEnabled = !busy;
        AppFileList.IsEnabled = !busy;
        UpdateAppBrowserButtons();
    }

    /// <summary>취소 가능한 작업을 시작하고 토큰을 돌려준다.</summary>
    private CancellationToken BeginOp()
    {
        CancelAppMedia();
        _thumbCts?.Cancel();
        _previewCts?.Cancel();
        _opCts?.Dispose();
        _opCts = new CancellationTokenSource();
        return _opCts.Token;
    }

    private void EndOp()
    {
        _opCts?.Dispose();
        _opCts = null;
    }

    private void UpdateCancelButton()
    {
        if (CancelBtn != null) CancelBtn.IsEnabled = _busy || _thumbCts != null || _previewCts != null || _appThumbCts != null || _appPreviewCts != null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _appThumbCts?.Cancel(); _appPreviewCts?.Cancel();
        _opCts?.Cancel();
        _thumbCts?.Cancel();
        _previewCts?.Cancel();
        StatusText.Text = "취소 중…";
    }

    private IProgress<TransferProgress> MakeProgress() => new Progress<TransferProgress>(p =>
    {
        Progress.IsIndeterminate = false;
        Progress.Value = p.Percent;
        StatusText.Text = p.BytesTotal > 0
            ? $"{p.Index}/{p.Total}  {p.CurrentFile}  ({FormatSize(p.BytesDone)} / {FormatSize(p.BytesTotal)})"
            : $"{p.Index}/{p.Total}  {p.CurrentFile}";
    });

    private void ShowError(System.Exception ex)
    {
        ConnectionSupport.Log($"Transfer: {ex.GetType().Name}: {ex.Message}");
        ConnectionTitle.Text = "작업을 완료하지 못했습니다";
        ConnectionDetail.Text = "연결 센터에서 상태를 확인하고 다시 시도하세요. " + ex.Message;
        var msg = ex is MobileDeviceException ? ex.Message : ex.Message;
        MessageBox.Show(msg, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        StatusText.Text = "오류 발생";
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
    }

    // ───────────── 기기 ─────────────

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await RefreshConnectionAsync(true);

    // ───────────── 가져오기 ─────────────

    private async void LoadPhotos_Click(object sender, RoutedEventArgs e) => await LoadPhotosAsync();

    private async Task LoadPhotosAsync()
    {
        if (_busy || !RequireDevice()) return;
        var ct = BeginOp();
        try
        {
            SetBusy(true, "사진 목록 불러오는 중…");
            _thumbCts?.Cancel();           // 이전 썸네일 로딩 중단
            _photos.Clear();
            var items = await IPhoneClient.ListPhotosAsync(CurrentDevice!.Udid, ct);
            foreach (var p in items)
            {
                var r = new PhotoRow(p);
                r.PropertyChanged += Row_PropertyChanged;
                _photos.Add(r);
            }
            _anchor = -1;
            _photoSummary = $"총 {items.Count}개 (전체 용량 {FormatSize(SumSize(items))})";
            UpdateSelectionCount();
            SetBusy(false, $"사진 {items.Count}개 불러옴");
            if (PhotoGrid.Visibility == Visibility.Visible) StartThumbnailLoad();
        }
        catch (OperationCanceledException) { SetBusy(false, "취소됨"); }
        catch (System.Exception ex) { SetBusy(false, "대기 중"); ShowError(ex); }
        finally { EndOp(); }
    }

    private void SelectAllPhotos_Click(object sender, RoutedEventArgs e)
    { foreach (var p in _photos) p.IsChecked = true; _anchor = 0; UpdateSelectionCount(); }

    private void UnselectAllPhotos_Click(object sender, RoutedEventArgs e)
    { foreach (var p in _photos) p.IsChecked = false; _anchor = -1; UpdateSelectionCount(); }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("HEIC/HEIF 사진은 내장 디코더로 표시합니다. 별도 Windows 코덱 설치가 필요 없습니다.\n\n" +
            "동영상은 필요한 구간만 읽어 썸네일을 표시하고 재사용합니다. 미리보기를 위해 영상 전체를 복사하지 않습니다. 재생하려면 PC로 가져오세요.\n\n" +
            "목록 자체에 원본이 없다면 아이폰 잠금과 iCloud 사진 설정을 확인하세요. iCloud에만 있는 원본은 아이폰에서 다운로드한 뒤 목록을 다시 불러오세요.\n\n" +
            "썸네일 실패 시 항목을 다시 선택하면 재시도합니다. 미리보기 실패와 관계없이 원본을 가져올 수 있습니다.\n\n" +
            "클릭: 한 장 선택 · Ctrl+클릭: 여러 장 · Shift+클릭: 범위 선택", "사진·영상 도움말", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void OpenUrl(string primary, string fallback)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(primary) { UseShellExecute = true }); }
        catch
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fallback) { UseShellExecute = true }); }
            catch { /* 무시 */ }
        }
    }

    // ───────────── 미리보기 ─────────────

    // ───────────── 클릭 선택 (Shift=범위, Ctrl=토글, 일반=단일) ─────────────

    private void Item_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListViewItem item || item.DataContext is not PhotoRow row) return;
        // 체크박스를 직접 클릭한 경우는 개별 토글(기본 동작)에 맡긴다.
        if (IsWithinCheckBox(e.OriginalSource as DependencyObject)) { _anchor = _photos.IndexOf(row); return; }

        int idx = _photos.IndexOf(row);
        if (idx < 0) return;
        if (e.ClickCount >= 2)
        {
            OpenLargeView(row);
            e.Handled = true;
            return;
        }
        var mods = Keyboard.Modifiers;

        if ((mods & ModifierKeys.Shift) != 0 && _anchor >= 0)
        {
            // 기준점~현재 사이 전체 선택
            int a = System.Math.Min(_anchor, idx), b = System.Math.Max(_anchor, idx);
            for (int i = 0; i < _photos.Count; i++) _photos[i].IsChecked = (i >= a && i <= b);
        }
        else if ((mods & ModifierKeys.Control) != 0)
        {
            // 개별 토글(다중 선택)
            row.IsChecked = !row.IsChecked;
            _anchor = idx;
        }
        else
        {
            // 단일 선택
            foreach (var p in _photos) p.IsChecked = false;
            row.IsChecked = true;
            _anchor = idx;
        }

        UpdateSelectionCount();
        ShowPreviewFor(row);
        e.Handled = true; // ListView 기본 선택 동작 억제
    }

    /// <summary>사진 또는 동영상 대표 프레임을 크게 본다.</summary>
    private void OpenLargeView(PhotoRow row)
    {
        if (CurrentDevice == null || _busy) return;
        _thumbCts?.Cancel();
        var viewer = new ImageViewerWindow(CurrentDevice.Udid, row.Item) { Owner = this };
        viewer.Closed += (_, _) => { if (!_busy && !_lifetime.IsCancellationRequested) StartThumbnailLoad(); };
        viewer.Show();
    }

    /// <summary>가져온 이미지 바이트를 JPG로 변환한다. 실패하면 null을 돌려줘 원본을 유지한다.</summary>
    private static byte[]? ConvertToJpeg(byte[] src)
        => MediaPreview.ConvertToJpeg(src);

    private static bool IsWithinCheckBox(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is CheckBox) return true;
            d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Row_PropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhotoRow.IsChecked)) UpdateSelectionCount();
    }

    private void UpdateSelectionCount()
    {
        int sel = _photos.Count(p => p.IsChecked);
        long selBytes = 0;
        foreach (var p in _photos) if (p.IsChecked) selBytes += p.Item.Size;
        var selPart = sel > 0 ? $"선택 {sel} ({FormatSize(selBytes)})" : "";
        PhotoCountText.Text = string.IsNullOrEmpty(_photoSummary)
            ? selPart
            : (sel > 0 ? $"{_photoSummary} · {selPart}" : _photoSummary);
    }

    // ───────────── 미리보기 ─────────────

    private async void ShowPreviewFor(PhotoRow row)
    {
        if (CurrentDevice == null || _busy) return;

        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        PreviewCaption.Text = $"{row.FileName}\n{row.DateText}  ·  {row.SizeText}";
        PreviewImage.Source = null;

        var udid = CurrentDevice.Udid;
        _thumbCts?.Cancel();
        CancelBtn.IsEnabled = true;
        PreviewMsg.Text = row.IsVideo ? "영상 썸네일을 불러오는 중…\n필요한 구간만 읽습니다." : "불러오는 중…";
        PreviewMsg.Visibility = Visibility.Visible;
        try
        {
            var img = row.IsVideo && row.Thumbnail is BitmapSource cached ? cached
                : await MediaPreview.LoadAsync(udid, row.Item, 640, cts.Token);
            if (cts.IsCancellationRequested) return;
            PreviewImage.Source = img;
            row.Thumbnail ??= img;
            row.PreviewError = null;
            PreviewMsg.Visibility = Visibility.Collapsed;
            if (row.IsVideo) PreviewCaption.Text += "\n영상 대표 프레임 · 가져온 뒤 재생 가능";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_previewCts, cts)) PreviewMsg.Text = "미리보기를 중지했습니다. 항목을 다시 선택하면 불러옵니다.";
        }
        catch (System.Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            PreviewMsg.Text = "미리보기를 읽지 못했습니다.\n잠금 해제 후 다시 선택하거나\n원본을 PC로 가져오세요.";
            row.PreviewError = ex.Message;
            PreviewMsg.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cts))
            {
                _previewCts = null;
                UpdateCancelButton();
                if (!cts.IsCancellationRequested && !_busy && !_lifetime.IsCancellationRequested && PhotoGrid.Visibility == Visibility.Visible)
                    StartThumbnailLoad();
            }
            cts.Dispose();
        }
    }

    // ───────────── 그리드(썸네일) 보기 ─────────────

    private void ViewList_Checked(object sender, RoutedEventArgs e)
    {
        if (PhotoList == null || PhotoGrid == null) return;
        PhotoList.Visibility = Visibility.Visible;
        PhotoGrid.Visibility = Visibility.Collapsed;
    }

    private void ViewGrid_Checked(object sender, RoutedEventArgs e)
    {
        if (PhotoList == null || PhotoGrid == null) return;
        PhotoList.Visibility = Visibility.Collapsed;
        PhotoGrid.Visibility = Visibility.Visible;
        StartThumbnailLoad();
    }

    /// <summary>사진 및 영상 미리보기를 순서대로 생성한다. 재시도와 취소는 현재 로드에만 반영한다.</summary>
    private async void StartThumbnailLoad()
    {
        if (CurrentDevice == null || _busy || MainTabs.SelectedIndex != 0) return;
        _thumbCts?.Cancel();
        var pending = _photos.Where(p => p.Thumbnail == null).ToList();
        if (pending.Count == 0) return;

        var cts = new CancellationTokenSource();
        _thumbCts = cts;
        CancelBtn.IsEnabled = true;
        var udid = CurrentDevice.Udid;

        int done = 0, ok = 0, fail = 0;
        StatusText.Text = $"썸네일 불러오는 중… (0/{pending.Count})";
        try
        {
            foreach (var row in pending)
            {
                cts.Token.ThrowIfCancellationRequested();
                if (row.Thumbnail != null) { done++; ok++; continue; }
                StatusText.Text = $"미리보기 {done + 1}/{pending.Count} · {row.FileName}";
                try
                {
                    var img = await MediaPreview.LoadAsync(udid, row.Item, 480, cts.Token);
                    cts.Token.ThrowIfCancellationRequested();
                    row.Thumbnail = img; row.PreviewError = null; ok++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { row.PreviewError = ex.Message; fail++; }
                done++;
            }
            if (!cts.IsCancellationRequested)
                StatusText.Text = fail > 0
                    ? $"미리보기 {ok}개 · 실패 {fail}개 — 항목을 선택하면 재시도합니다. 원본 가져오기도 가능합니다."
                    : $"썸네일 완료 — {ok}개";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_thumbCts, cts) && !_busy && _previewCts == null)
                StatusText.Text = "미리보기 불러오기를 중지했습니다. 그리드 보기를 다시 선택하면 이어서 불러옵니다.";
        }
        catch (System.Exception) { if (!cts.IsCancellationRequested) StatusText.Text = "썸네일 일부 실패"; }
        finally
        {
            if (ReferenceEquals(_thumbCts, cts)) { _thumbCts = null; UpdateCancelButton(); }
            cts.Dispose();
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !RequireDevice()) return;
        var selected = _photos.Where(p => p.IsChecked).Select(p => p.Item).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("가져올 사진을 한 개 이상 선택하세요.", "선택 없음", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new OpenFolderDialog { Title = "저장할 폴더 선택" };
        if (_importFolder != null) dlg.InitialDirectory = _importFolder;
        if (dlg.ShowDialog() != true) return;
        _importFolder = dlg.FolderName;
        ImportDestText.Text = "→ " + _importFolder;

        // 저장 폴더가 있는 드라이브의 여유 공간을 미리 확인
        long need = SumSize(selected);
        try
        {
            var root = Path.GetPathRoot(_importFolder);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace < need)
                {
                    MessageBox.Show(
                        $"저장 폴더의 여유 공간이 부족합니다.\n필요: {FormatSize(need)}\n남은 공간: {FormatSize(drive.AvailableFreeSpace)}",
                        "공간 부족", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
        }
        catch { /* 공간 확인 실패는 진행을 막지 않음 */ }

        var ct = BeginOp();
        try
        {
            SetBusy(true, "가져오는 중…");
            await IPhoneClient.ImportPhotosAsync(CurrentDevice!.Udid, selected, _importFolder, MakeProgress(), ct,
                ConvertJpegCheck.IsChecked == true ? ConvertToJpeg : null);
            SetBusy(false, $"완료 — {selected.Count}개 저장됨");
            if (MessageBox.Show($"{selected.Count}개 가져오기 완료!\n폴더를 열까요?", "완료",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start("explorer.exe", $"\"{_importFolder}\"");
        }
        catch (OperationCanceledException) { SetBusy(false, "취소됨 — 일부만 저장되었을 수 있습니다"); }
        catch (System.Exception ex) { SetBusy(false, "대기 중"); ShowError(ex); }
        finally { EndOp(); }
    }

    // ───────────── 앱으로 보내기 ─────────────

    private async void LoadApps_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !RequireDevice()) return;
        var ct = BeginOp();
        try
        {
            SetBusy(true, "파일 공유 앱 검색 중…");
            var apps = await IPhoneClient.ListSharingAppsAsync(CurrentDevice!.Udid, ct);
            AppCombo.ItemsSource = apps;
            if (apps.Count > 0) AppCombo.SelectedIndex = 0;
            SetBusy(false, apps.Count > 0
                ? $"파일 공유 앱 {apps.Count}개 찾음"
                : "파일 공유 지원 앱이 없습니다 (VLC, Documents 등 설치 필요)");
        }
        catch (OperationCanceledException) { SetBusy(false, "취소됨"); }
        catch (System.Exception ex) { SetBusy(false, "대기 중"); ShowError(ex); }
        finally { EndOp(); }
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "보낼 파일 선택" };
        if (dlg.ShowDialog() == true)
            foreach (var f in dlg.FileNames) AddFile(f);
    }

    private void FilesToSend_Drop(object sender, DragEventArgs e)
    {
        if (_busy) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            foreach (var p in paths)
                if (File.Exists(p)) AddFile(p);
    }

    private void AddFile(string path)
    {
        if (!_filesToSend.Contains(path)) _filesToSend.Add(path);
    }

    private void ClearFiles_Click(object sender, RoutedEventArgs e) => _filesToSend.Clear();

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !RequireDevice()) return;
        if (AppCombo.SelectedItem is not SharingApp app)
        {
            MessageBox.Show("보낼 앱을 선택하세요. 위에서 '파일 공유 앱 목록 불러오기'를 먼저 누르세요.",
                "앱 미선택", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_filesToSend.Count == 0)
        {
            MessageBox.Show("보낼 파일을 추가하세요.", "파일 없음", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var ct = BeginOp();
        try
        {
            SetBusy(true, "보내는 중…");
            await IPhoneClient.SendFilesToAppAsync(CurrentDevice!.Udid, app.BundleId, _filesToSend.ToList(), MakeProgress(), ct);
            SetBusy(false, $"완료 — '{app.DisplayName}'(으)로 {_filesToSend.Count}개 전송");
            MessageBox.Show($"'{app.DisplayName}' 앱으로 {_filesToSend.Count}개 전송 완료!\n" +
                            "아이폰에서 해당 앱을 열어 확인하세요.", "완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { SetBusy(false, "취소됨 — 일부만 전송되었을 수 있습니다"); }
        catch (System.Exception ex) { SetBusy(false, "대기 중"); ShowError(ex); }
        finally { EndOp(); }
    }

    // ───────────── 유틸 ─────────────

    private static long SumSize(IEnumerable<PhotoItem> items)
    { long s = 0; foreach (var i in items) s += i.Size; return s; }

    private static string FormatSize(long bytes)
        => bytes >= 1L << 30 ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB"
         : bytes >= 1 << 20 ? $"{bytes / (1024.0 * 1024):0.0} MB"
         : $"{bytes / 1024.0:0} KB";
}

/// <summary>체크박스 상태를 가진 사진 목록 행.</summary>
public sealed class PhotoRow : INotifyPropertyChanged
{
    private static readonly HashSet<string> Vid = new(StringComparer.OrdinalIgnoreCase)
    { ".mov", ".mp4", ".m4v", ".avi" };

    public PhotoItem Item { get; }
    public bool IsVideo { get; }

    public PhotoRow(PhotoItem item)
    {
        Item = item;
        IsVideo = Vid.Contains(Path.GetExtension(item.FileName));
    }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked != value) { _isChecked = value; OnChanged(nameof(IsChecked)); } }
    }

    private System.Windows.Media.ImageSource? _thumb;
    public System.Windows.Media.ImageSource? Thumbnail
    {
        get => _thumb;
        set { _thumb = value; OnChanged(nameof(Thumbnail)); }
    }

    private string? _previewError;
    public string? PreviewError
    {
        get => _previewError;
        set { _previewError = value; OnChanged(nameof(Placeholder)); OnChanged(nameof(Tooltip)); }
    }
    public string Tooltip => PreviewError == null ? FileName : FileName + "\n" + PreviewError;
    public string Placeholder => PreviewError != null ? "미리보기 재시도\n항목을 선택하세요" : IsVideo ? "🎬" : "🖼";
    public string MediaLabel => IsVideo ? "영상" : Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant();
    public string FileName => Item.FileName;
    public string SizeText => Item.SizeText;
    public string DateText => Item.DateText;
    public string DevicePath => Item.DevicePath;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new(n));
}
