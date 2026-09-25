using System.ComponentModel;
using System.Windows.Media;
using iPhoneTransfer.Core;

namespace iPhoneTransfer.App;

public sealed class AppFileRow : INotifyPropertyChanged
{
    public AppFileItem Item { get; }
    public AppFileRow(AppFileItem item) => Item = item;
    public AppFileRow(string path, string name, bool directory, long size, DateTime modified)
        : this(new AppFileItem(path, name, directory, size, modified)) { }
    public string Name => Item.Name;
    public string DevicePath => Item.DevicePath;
    public bool IsDirectory => Item.IsDirectory;
    public bool CanPreview => !Item.IsDirectory && !Item.IsSymbolicLink && (MediaPreview.IsImage(Name) || IsVideo);
    public bool IsVideo => MediaPreview.IsVideo(Name);
    public string Kind => Item.Kind;
    public string SizeText => Item.SizeText;
    public string DateText => Item.DateText;
    public string MediaLabel => Item.IsDirectory ? "폴더" : IsVideo ? "영상" : System.IO.Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();
    public string Detail => Item.IsDirectory ? "더블클릭으로 열기" : SizeText;
    public string Placeholder => Item.IsDirectory ? "📁" : Item.IsSymbolicLink ? "링크" : PreviewError != null
        ? "미리보기 재시도\n항목을 선택하세요" : CanPreview ? (IsVideo ? "영상 불러오는 중…" : "사진 불러오는 중…") : "📄";
    public string Tooltip => $"{Name}\n{DateText} · {SizeText}" + (PreviewError == null ? "" : "\n" + PreviewError);
    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; Changed(nameof(Thumbnail)); }
    }
    private string? _previewError;
    public string? PreviewError
    {
        get => _previewError;
        set { _previewError = value; Changed(nameof(Placeholder)); Changed(nameof(Tooltip)); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
