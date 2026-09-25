using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using iPhoneTransfer.Core;

namespace iPhoneTransfer.App;

/// <summary>
/// 사진 한 장을 큰 창에서 보는 뷰어.
/// 마우스 휠 = 확대/축소, 드래그 = 이동, 더블클릭 = 원래대로, ESC = 닫기.
/// </summary>
public sealed class ImageViewerWindow : Window
{
    private readonly string _udid;
    private readonly PhotoItem _item;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translate = new(0, 0);
    private readonly TextBlock _msg = new()
    {
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 14
    };

    private CancellationTokenSource? _cts;
    private Point _lastDrag;
    private bool _dragging;

    public ImageViewerWindow(string udid, PhotoItem item)
    {
        _udid = udid;
        _item = item;

        Title = $"{item.FileName} — {(MediaPreview.IsVideo(item.FileName) ? "영상 대표 프레임" : "크게보기")} (휠=확대/축소, 드래그=이동, ESC=닫기)";
        Width = 1000;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.Black;

        _image.RenderTransformOrigin = new Point(0.5, 0.5);
        var group = new TransformGroup();
        group.Children.Add(_scale);
        group.Children.Add(_translate);
        _image.RenderTransform = group;

        var root = new Grid { ClipToBounds = true };
        root.Children.Add(_image);
        root.Children.Add(_msg);
        Content = root;

        _msg.Text = "불러오는 중…";

        MouseWheel += OnWheel;
        MouseLeftButtonDown += OnMouseDown;
        MouseLeftButtonUp += OnMouseUp;
        MouseMove += OnMouseMove;
        MouseDoubleClick += (_, _) => ResetView();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    private void ResetView()
    {
        _scale.ScaleX = _scale.ScaleY = 1;
        _translate.X = _translate.Y = 0;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
        double next = Math.Clamp(_scale.ScaleX * factor, 0.2, 10.0);
        _scale.ScaleX = _scale.ScaleY = next;
        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _lastDrag = e.GetPosition(this);
        _image.CaptureMouse();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        _image.ReleaseMouseCapture();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        _translate.X += p.X - _lastDrag.X;
        _translate.Y += p.Y - _lastDrag.Y;
        _lastDrag = p;
    }

    private async Task LoadAsync()
    {
        _cts = new CancellationTokenSource();
        try
        {
            var bmp = await MediaPreview.LoadAsync(_udid, _item, MediaPreview.IsVideo(_item.FileName) ? 1920 : 0, _cts.Token);
            _cts.Token.ThrowIfCancellationRequested();
            _image.Source = bmp;
            _msg.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { /* 창이 닫힘 */ }
        catch (Exception)
        {
            _msg.Text = "미리보기를 불러오지 못했습니다.\n아이폰 잠금을 해제하고 다시 시도하거나 원본을 PC로 가져오세요.";
        }
        finally { _cts.Dispose(); _cts = null; }
    }

}
