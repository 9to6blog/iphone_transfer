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
    private readonly string _devicePath;
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

    public ImageViewerWindow(string udid, string devicePath, string fileName)
    {
        _udid = udid;
        _devicePath = devicePath;

        Title = $"{fileName} — 크게보기 (휠=확대/축소, 드래그=이동, 더블클릭=원래대로, ESC=닫기)";
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
            var bytes = await IPhoneClient.ReadFileBytesAsync(_udid, _devicePath, 0, _cts.Token);
            var bmp = Decode(bytes);
            if (bmp == null)
            {
                _msg.Text = "이미지를 표시할 수 없습니다.\n(HEIC 코덱 미설치 등 — 메인 창의 '❓ 일부만 보일 때' 참고)";
                return;
            }
            _image.Source = bmp;
            _msg.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { /* 창이 닫힘 */ }
        catch (Exception)
        {
            _msg.Text = "이미지를 불러오지 못했습니다.";
        }
    }

    private static BitmapImage? Decode(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
