using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using iPhoneTransfer.Core;

namespace iPhoneTransfer.App;

public partial class MainWindow
{
    private CancellationTokenSource? _appThumbCts, _appPreviewCts;
    private ImageViewerWindow? _appViewer;
    private Func<string, string, AppFileItem, int, CancellationToken, Task<BitmapSource>> _appMediaLoader = MediaPreview.LoadAppAsync;

    private bool AppBrowserActive => MainTabs?.SelectedIndex == 1 && AppReadRadio?.IsChecked == true && !_busy && !_lifetime.IsCancellationRequested;
    private bool AppContextValid(string device, string bundle) => AppBrowserActive && CurrentDevice?.IsReady == true &&
        CurrentDevice.Udid == device && _appFilesDevice == device && _appFilesBundle == bundle && (AppCombo.SelectedItem as SharingApp)?.BundleId == bundle;

    private void CancelAppSelectedPreview()
    {
        _appPreviewCts?.Cancel(); _appPreviewCts = null;
    }

    private void CancelAppMedia()
    {
        _appThumbCts?.Cancel(); _appThumbCts = null;
        CancelAppSelectedPreview();
        var viewer = _appViewer; _appViewer = null; viewer?.Close();
        UpdateCancelButton();
    }

    private void ClearAppPreview()
    {
        if (AppPreviewImage == null) return;
        AppPreviewImage.Source = null; AppPreviewCaption.Text = "";
        AppPreviewMessage.Text = AppFileList.SelectedItems.Count > 1 ? $"{AppFileList.SelectedItems.Count}개 항목 선택됨"
            : "사진·영상을 선택하면\n미리보기가 표시됩니다.\n\n더블클릭으로 크게 보기";
        AppPreviewMessage.Visibility = Visibility.Visible;
    }

    private void AppView_Checked(object sender, RoutedEventArgs e)
    {
        if (AppFileList == null) return;
        var grid = AppGridRadio.IsChecked == true;
        AppFileList.View = grid ? null : (GridView)FindResource("AppDetailsView");
        AppFileList.ItemTemplate = grid ? (DataTemplate)FindResource("AppTileTemplate") : null;
        AppFileList.ItemsPanel = (ItemsPanelTemplate)FindResource(grid ? "AppWrapPanel" : "AppStackPanel");
        StartAppThumbnailLoad();
    }

    private async void StartAppThumbnailLoad()
    {
        var device = _appFilesDevice; var bundle = _appFilesBundle;
        if (device == null || bundle == null || !AppContextValid(device, bundle) || _appPreviewCts != null || _appViewer != null) return;
        _appThumbCts?.Cancel();
        var pending = _appFiles.Where(r => r.CanPreview && r.Thumbnail == null && r.PreviewError == null)
            .OrderBy(r => r.IsVideo).ThenBy(r => r.Item.Size).ToArray();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _appThumbCts = cts;
        CancelBtn.IsEnabled = pending.Length > 0;
        try
        {
            int done = 0;
            foreach (var row in pending)
            {
                cts.Token.ThrowIfCancellationRequested();
                StatusText.Text = $"앱 미리보기 {++done}/{pending.Length} · {row.Name}";
                try
                {
                    var bitmap = await _appMediaLoader(device, bundle, row.Item, 480, cts.Token);
                    cts.Token.ThrowIfCancellationRequested();
                    if (!AppContextValid(device, bundle) || !_appFiles.Contains(row)) return;
                    row.Thumbnail = bitmap;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { if (!cts.IsCancellationRequested) row.PreviewError = ex.Message; }
            }
            if (pending.Length > 0 && AppContextValid(device, bundle))
            {
                var failed = _appFiles.Count(r => r.PreviewError != null);
                StatusText.Text = failed == 0 ? "앱 미리보기 완료 · 더블클릭으로 크게 볼 수 있습니다."
                    : $"미리보기 실패 {failed}개 · 항목 선택으로 재시도하거나 원본을 가져올 수 있습니다.";
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_appThumbCts, cts) && AppBrowserActive)
                StatusText.Text = "앱 미리보기 중지됨 · 보기 방식을 다시 선택하면 이어서 불러옵니다.";
        }
        finally
        {
            if (ReferenceEquals(_appThumbCts, cts)) { _appThumbCts = null; UpdateCancelButton(); }
            cts.Dispose();
        }
    }

    private async Task ShowAppPreviewAsync(AppFileRow row)
    {
        CancelAppSelectedPreview();
        ClearAppPreview();
        var device = _appFilesDevice; var bundle = _appFilesBundle;
        if (device == null || bundle == null || !AppContextValid(device, bundle)) return;
        AppPreviewCaption.Text = $"{row.Name}\n{row.DateText} · {row.SizeText}";
        if (!row.CanPreview)
        {
            AppPreviewMessage.Text = row.IsDirectory ? "폴더를 더블클릭해\n안의 파일을 볼 수 있습니다."
                : "이 형식은 미리보기를 지원하지 않습니다.\nPC로 가져와 열어 주세요.";
            StartAppThumbnailLoad();
            return;
        }
        _appThumbCts?.Cancel(); _appThumbCts = null;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _appPreviewCts = cts; CancelBtn.IsEnabled = true;
        AppPreviewMessage.Text = row.IsVideo ? "영상 대표 화면을 불러오는 중…\n큰 영상은 시간이 걸릴 수 있습니다." : "사진을 불러오는 중…";
        try
        {
            var bitmap = row.Thumbnail as BitmapSource ?? await _appMediaLoader(device, bundle, row.Item, 800, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!AppContextValid(device, bundle) || AppFileList.SelectedItem != row) return;
            row.Thumbnail ??= bitmap; row.PreviewError = null;
            AppPreviewImage.Source = bitmap; AppPreviewMessage.Visibility = Visibility.Collapsed;
            if (row.IsVideo) AppPreviewCaption.Text += "\n영상 대표 화면 · 가져온 뒤 재생 가능";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_appPreviewCts, cts))
            { AppPreviewMessage.Text = "미리보기 중지됨\n다시 선택하거나 더블클릭해 주세요."; StatusText.Text = "앱 미리보기를 중지했습니다."; }
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && AppContextValid(device, bundle))
            { row.PreviewError = ex.Message; AppPreviewMessage.Text = "미리보기를 읽지 못했습니다.\n더블클릭으로 재시도하거나\n원본을 PC로 가져오세요."; }
        }
        finally
        {
            if (ReferenceEquals(_appPreviewCts, cts))
            {
                _appPreviewCts = null; UpdateCancelButton();
                if (!cts.IsCancellationRequested) StartAppThumbnailLoad();
            }
            cts.Dispose();
        }
    }

    private void AppLargePreview_Click(object sender, RoutedEventArgs e)
    { if (AppFileList.SelectedItem is AppFileRow row) OpenAppLargeView(row); }

    private void OpenAppLargeView(AppFileRow row)
    {
        var device = _appFilesDevice; var bundle = _appFilesBundle;
        if (!row.CanPreview || device == null || bundle == null || !AppContextValid(device, bundle)) return;
        CancelAppMedia();
        var viewer = new ImageViewerWindow(row.Name, ct => _appMediaLoader(device, bundle, row.Item, row.IsVideo ? 1920 : 0, ct)) { Owner = this };
        _appViewer = viewer;
        viewer.Closed += (_, _) => { if (_appViewer == viewer) { _appViewer = null; StartAppThumbnailLoad(); } };
        viewer.Show();
    }
}
