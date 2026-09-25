using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using ImageMagick;
using iPhoneTransfer.Core;

namespace iPhoneTransfer.App;

internal static class MediaPreview
{
    // Limit concurrent native decoders and USB preview transfers. Originals are never changed.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static string FfmpegPath => Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
    internal static bool IsVideo(string name) => Path.GetExtension(name).ToLowerInvariant() is ".mov" or ".mp4" or ".m4v" or ".avi";

    internal static async Task<BitmapSource> LoadAsync(string udid, PhotoItem item, int width, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        var folder = Path.Combine(Path.GetTempPath(), "iPhoneTransfer-preview", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "source" + Path.GetExtension(item.FileName));
            await IPhoneClient.CopyPhotoToFileAsync(udid, item, path, ct);
            ct.ThrowIfCancellationRequested();
            return IsVideo(item.FileName)
                ? await VideoFrameAsync(path, width, ct)
                : await Task.Run(() => DecodeImage(path, width), ct);
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { }
            Gate.Release();
        }
    }

    internal static BitmapSource DecodeImage(string path, int width)
    {
        using var image = new MagickImage(path);
        return DecodeImage(image, width);
    }

    internal static BitmapSource DecodeImage(byte[] bytes, int width)
    {
        using var image = new MagickImage(bytes);
        return DecodeImage(image, width);
    }

    private static BitmapSource DecodeImage(MagickImage image, int width)
    {
        image.AutoOrient();
        if (width > 0) image.Resize(new MagickGeometry((uint)width, (uint)width) { Greater = true });
        return DecodePng(image.ToByteArray(MagickFormat.Png));
    }

    internal static byte[]? ConvertToJpeg(byte[] bytes)
    {
        try
        {
            using var image = new MagickImage(bytes);
            image.AutoOrient();
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
            image.Quality = 92;
            return image.ToByteArray(MagickFormat.Jpeg);
        }
        catch { return null; } // Import retains the exact original if conversion fails.
    }

    internal static BitmapSource DecodePng(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    internal static async Task<BitmapSource> VideoFrameAsync(string path, int width, CancellationToken ct)
    {
        var start = new ProcessStartInfo(FfmpegPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        // A local seekable file handles iPhone MOV files whose index is at the end.
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "2",
                     "-i", path, "-map", "0:v:0", "-frames:v", "1", "-vf",
                     $"scale={width}:{width}:force_original_aspect_ratio=decrease", "-threads", "2",
                     "-f", "image2pipe", "-c:v", "png", "pipe:1" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("동영상 미리보기를 시작하지 못했습니다.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await Task.WhenAll(copy, process.WaitForExitAsync(timeout.Token));
            var error = await errors;
            if (process.ExitCode != 0 || output.Length == 0)
                throw new IOException("동영상 프레임을 읽지 못했습니다. 원본을 가져와 재생해 주세요.");
            ct.ThrowIfCancellationRequested();
            return DecodePng(output.ToArray());
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            await process.WaitForExitAsync();
            try { await copy; } catch { }
            await errors;
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("동영상 미리보기 시간이 초과되었습니다. 원본은 가져올 수 있습니다.");
        }
    }
}
