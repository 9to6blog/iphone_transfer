using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using iPhoneTransfer.Core;
using Microsoft.Win32;
using System.ComponentModel;
using System.Windows.Data;

namespace iPhoneTransfer.App;

public partial class MainWindow
{
    private readonly ObservableCollection<AppFileRow> _appFiles = new();
    private string _appDirectory = "/Documents";
    private string? _appFilesBundle, _appFilesDevice, _appImportFolder;
    private bool _sortingAppFiles;

    private void AppSort_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyAppSort();

    private void ApplyAppSort()
    {
        if (AppFileList?.ItemsSource == null || AppSortCombo == null) return;
        _appThumbCts?.Cancel(); _appThumbCts = null;
        _sortingAppFiles = true;
        try
        {
            var view = CollectionViewSource.GetDefaultView(_appFiles);
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                if (AppSortCombo.SelectedIndex != 2)
                {
                    view.SortDescriptions.Add(new(nameof(AppFileRow.HasModified), ListSortDirection.Descending));
                    view.SortDescriptions.Add(new("Item.Modified", AppSortCombo.SelectedIndex == 1 ? ListSortDirection.Ascending : ListSortDirection.Descending));
                }
                view.SortDescriptions.Add(new(nameof(AppFileRow.Name), ListSortDirection.Ascending));
            }
        }
        finally { _sortingAppFiles = false; }
        UpdateAppBrowserButtons();
        StartAppThumbnailLoad();
    }

    private AppFileRow[] SelectedAppItemsInDisplayOrder() => AppFileList.Items.Cast<AppFileRow>()
        .Where(row => AppFileList.SelectedItems.Contains(row)).ToArray();

    private void AppDirection_Checked(object sender, RoutedEventArgs e)
    {
        if (AppReadPane == null || AppSendPane == null) return;
        AppReadPane.Visibility = AppReadRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AppSendPane.Visibility = AppReadRadio.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        if (AppGridRadio != null) AppGridRadio.Visibility = AppListRadio.Visibility = AppReadPane.Visibility;
        CancelAppMedia();
        if (AppReadRadio.IsChecked == true) StartAppThumbnailLoad();
    }

    private void AppCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ResetAppBrowser();

    private void ResetAppBrowser()
    {
        if (AppFileList == null) return;
        CancelAppMedia();
        _appFiles.Clear(); _appDirectory = "/Documents"; _appFilesBundle = null; _appFilesDevice = null;
        ClearAppPreview();
        AppFolderText.Text = _appDirectory;
        AppFilesEmpty.Text = "앱을 선택하고 ‘앱 파일 불러오기’를 누르세요.";
        AppFilesEmpty.Visibility = Visibility.Visible;
        UpdateAppBrowserButtons();
    }

    private void UpdateAppBrowserButtons()
    {
        if (ImportAppFilesBtn == null) return;
        var app = AppCombo.SelectedItem as SharingApp;
        var hasApp = app != null && CurrentDevice?.IsReady == true;
        var validList = hasApp && app!.BundleId == _appFilesBundle && CurrentDevice!.Udid == _appFilesDevice;
        LoadAppFilesBtn.IsEnabled = !_busy && hasApp;
        AppParentBtn.IsEnabled = !_busy && validList && _appDirectory != "/Documents";
        AppSelectAllBtn.IsEnabled = AppClearSelectionBtn.IsEnabled = !_busy && validList && _appFiles.Count > 0;
        ImportAppFilesBtn.IsEnabled = !_busy && validList && AppFileList.SelectedItems.Count > 0;
        AppLargePreviewBtn.IsEnabled = !_busy && validList && AppFileList.SelectedItems.Count == 1 && AppFileList.SelectedItem is AppFileRow { CanPreview: true };
        AppSelectionText.Text = $"{_appFiles.Count}개 항목 · 선택 {AppFileList.SelectedItems.Count}개";
    }

    private async void LoadAppFiles_Click(object sender, RoutedEventArgs e) => await LoadAppDirectoryAsync(_appDirectory);
    private async void AppParent_Click(object sender, RoutedEventArgs e)
    {
        if (_appDirectory != "/Documents") await LoadAppDirectoryAsync(_appDirectory[.._appDirectory.LastIndexOf('/')]);
    }
    private void AppSelectAll_Click(object sender, RoutedEventArgs e) { if (!_busy) AppFileList.SelectAll(); }
    private void AppClearSelection_Click(object sender, RoutedEventArgs e) { if (!_busy) AppFileList.UnselectAll(); }
    private async void AppFileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAppBrowserButtons();
        if (_sortingAppFiles) return;
        if (AppFileList.SelectedItems.Count == 1 && AppFileList.SelectedItem is AppFileRow row) await ShowAppPreviewAsync(row);
        else { CancelAppSelectedPreview(); ClearAppPreview(); StartAppThumbnailLoad(); }
    }
    private async void AppFileList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(AppFileList, e.OriginalSource as DependencyObject) is ListViewItem { DataContext: AppFileRow row })
        {
            if (row.IsDirectory) await LoadAppDirectoryAsync(row.DevicePath);
            else OpenAppLargeView(row);
        }
    }
    private async void AppFileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && AppFileList.SelectedItem is AppFileRow row)
        { e.Handled = true; if (row.IsDirectory) await LoadAppDirectoryAsync(row.DevicePath); else OpenAppLargeView(row); }
        else if (e.Key == Key.Back && _appDirectory != "/Documents")
        { e.Handled = true; await LoadAppDirectoryAsync(_appDirectory[.._appDirectory.LastIndexOf('/')]); }
    }

    private async Task LoadAppDirectoryAsync(string directory)
    {
        if (_busy || !RequireDevice() || AppCombo.SelectedItem is not SharingApp app) return;
        var device = CurrentDevice!.Udid;
        var ct = BeginOp();
        try
        {
            SetBusy(true, "앱 파일 목록 불러오는 중…");
            var items = await IPhoneClient.ListAppFilesAsync(device, app.BundleId, directory, ct);
            ct.ThrowIfCancellationRequested();
            _appFiles.Clear();
            foreach (var item in items) _appFiles.Add(new(item));
            _appDirectory = directory; _appFilesBundle = app.BundleId; _appFilesDevice = device;
            AppFolderText.Text = directory;
            AppFilesEmpty.Text = "이 폴더는 비어 있습니다.";
            AppFilesEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetBusy(false, $"앱 파일 {items.Count}개 — 폴더를 열거나 선택 항목을 가져오세요.");
        }
        catch (OperationCanceledException) { SetBusy(false, "앱 파일 조회 취소됨"); }
        catch (Exception ex) { SetBusy(false, "앱 파일 조회 실패"); ShowError(ex); }
        finally { EndOp(); StartAppThumbnailLoad(); }
    }

    private async void ImportAppFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !RequireDevice() || AppCombo.SelectedItem is not SharingApp app ||
            app.BundleId != _appFilesBundle || CurrentDevice!.Udid != _appFilesDevice) return;
        var selected = SelectedAppItemsInDisplayOrder();
        if (selected.Length == 0) return;
        var dialog = new OpenFolderDialog { Title = "앱 파일을 저장할 PC 폴더 선택" };
        if (_appImportFolder != null) dialog.InitialDirectory = _appImportFolder;
        if (dialog.ShowDialog(this) != true) return;
        _appImportFolder = dialog.FolderName;
        var ct = BeginOp();
        try
        {
            SetBusy(true, "하위 폴더와 파일 확인 후 가져오는 중…");
            var result = await IPhoneClient.ImportAppFilesAsync(CurrentDevice!.Udid, app.BundleId,
                selected.Select(i => i.DevicePath).ToArray(), _appImportFolder, MakeProgress(), ct);
            SetBusy(false, $"가져오기 완료 — 파일 {result.Files}개 · 폴더 {result.Folders}개");
            MessageBox.Show(this, $"파일 {result.Files}개와 폴더 {result.Folders}개를 저장했습니다.\n\n{_appImportFolder}\n\n아이폰 앱의 원본은 그대로 보관됩니다.",
                "앱에서 가져오기 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { SetBusy(false, "가져오기 취소됨 — 완료된 파일은 PC에 보관됩니다."); }
        catch (Exception ex) { SetBusy(false, "가져오기 중단 — 완료된 파일은 PC에 보관됩니다."); ShowError(ex); }
        finally { EndOp(); }
    }
}
