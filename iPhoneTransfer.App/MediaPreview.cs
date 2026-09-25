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
    private sealed record VideoKey(string Device, string? App, string Path, long Size, DateTime Modified);
    private static readonly Dictionary<VideoKey, BitmapSource> VideoCache = new();
    private static readonly Queue<VideoKey> VideoOrder = new();
    internal sealed record VideoReadStats(long SourceBytes, long UsbBytes, bool CacheHit, int? FirstFrameBytes = null, long? MetadataBytes = null);
    internal static VideoReadStats? LastVideoRead { get; private set; }
    internal static string FfmpegPath => Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
    internal static bool IsVideo(string name) => Path.GetExtension(name).ToLowerInvariant() is ".mov" or ".mp4" or ".m4v" or ".avi" or ".mkv" or ".webm";
    internal static bool IsImage(string name) => Path.GetExtension(name).ToLowerInvariant() is
        ".jpg" or ".jpeg" or ".png" or ".heic" or ".heif" or ".tif" or ".tiff" or ".gif" or ".bmp" or ".webp" or ".dng" or ".avif";

    internal static Task<BitmapSource> LoadAsync(string udid, PhotoItem item, int width, CancellationToken ct)
        => IsVideo(item.FileName) ? LoadVideoAsync(udid, null, item, ct)
            : LoadFileAsync(item.FileName, width, (path, token) => IPhoneClient.CopyPhotoToFileAsync(udid, item, path, token), ct);

    internal static Task<BitmapSource> LoadAppAsync(string udid, string bundleId, AppFileItem item, int width, CancellationToken ct)
        => IsVideo(item.Name) ? LoadVideoAsync(udid, bundleId, new(item.DevicePath, item.Name, item.Size, item.Modified), ct)
            : LoadFileAsync(item.Name, width, (path, token) => IPhoneClient.CopyAppFileToFileAsync(udid, bundleId, item, path, token), ct);

    private static async Task<BitmapSource> LoadVideoAsync(string udid, string? bundleId, PhotoItem item, CancellationToken ct)
    {
        var key = new VideoKey(udid, bundleId, item.DevicePath, item.Size, item.Modified);
        await Gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (VideoCache.TryGetValue(key, out var cached)) { LastVideoRead = new(item.Size, 0, true); return cached; }
            var bitmap = await IPhoneClient.WithMediaReaderAsync(udid, bundleId, item,
                async reader =>
                {
                    int? sampleBytes = null; long? metadataBytes = null;
                    var frame = await VideoFrameFromReaderAsync(reader, item.FileName, 640, ct,
                        (sample, metadata) => { sampleBytes = sample; metadataBytes = metadata; });
                    LastVideoRead = new(reader.Length, reader.BytesRead, false, sampleBytes, metadataBytes);
                    return frame;
                }, ct);
            ct.ThrowIfCancellationRequested();
            VideoCache.Add(key, bitmap); VideoOrder.Enqueue(key);
            while (VideoOrder.Count > 24) VideoCache.Remove(VideoOrder.Dequeue());
            return bitmap;
        }
        finally { Gate.Release(); }
    }

    internal static async Task<BitmapSource> VideoFrameFromReaderAsync(IMediaReader reader, string name, int width, CancellationToken ct,
        Action<int, long>? firstSampleRead = null)
    {
        ct.ThrowIfCancellationRequested();
        if (Path.GetExtension(name).ToLowerInvariant() is ".mov" or ".mp4" or ".m4v")
        {
            FirstMovieFrame.Clip? clip = null;
            try { clip = await Task.Run(() => FirstMovieFrame.Read(reader, ct), ct); }
            catch (FirstMovieFrame.Unsupported) { /* Other codecs/fragmented movies use the small, bounded fallback below. */ }
            if (clip != null)
            {
                firstSampleRead?.Invoke(clip.SampleBytes, clip.MetadataBytes);
                var folder = Path.Combine(Path.GetTempPath(), "iPhoneTransfer-preview", Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(folder);
                    var path = Path.Combine(folder, "first-frame.mp4");
                    await File.WriteAllBytesAsync(path, clip.Bytes, ct);
                    return await VideoFrameAsync(path, width, ct);
                }
                finally { try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { } }
            }
        }
        await using var server = new VideoRangeServer(new PreviewReadBudget(reader), ct);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, server.Failure);
        try { return await VideoFrameAsync(server.Url, width, lifetime.Token, name); }
        catch (Exception) when (server.Error != null && !ct.IsCancellationRequested)
        { throw new IOException("영상 썸네일을 부분 읽기로 만들지 못했습니다. 전체 영상은 다운로드하지 않습니다.", server.Error); }
    }

    // A fallback must not quietly grow into another large metadata scan.
    private sealed class PreviewReadBudget(IMediaReader source) : IMediaReader
    {
        public long Length => source.Length;
        public long BytesRead => source.BytesRead;
        public int ReadAt(long offset, byte[] buffer, int count, CancellationToken ct)
        {
            if (source.BytesRead + count > 512 * 1024) throw new IOException("빠른 썸네일 읽기 한도에 도달했습니다. 원본을 가져와 재생해 주세요.");
            return source.ReadAt(offset, buffer, count, ct);
        }
    }

    internal static async Task<BitmapSource> LoadFileAsync(string name, int width,
        Func<string, CancellationToken, Task> copyFile, CancellationToken ct)
    {
        if (IsVideo(name)) throw new InvalidOperationException("영상 미리보기는 전체 파일 복사를 사용할 수 없습니다.");
        await Gate.WaitAsync(ct);
        var folder = Path.Combine(Path.GetTempPath(), "iPhoneTransfer-preview", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "source" + Path.GetExtension(name));
            await copyFile(path, ct);
            ct.ThrowIfCancellationRequested();
            return await Task.Run(() => DecodeImage(path, width), ct);
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

    internal static async Task<BitmapSource> VideoFrameAsync(string path, int width, CancellationToken ct, string? remoteName = null)
    {
        var start = new ProcessStartInfo(FfmpegPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "2" }) start.ArgumentList.Add(arg);
        if (remoteName != null)
        {
            var format = Path.GetExtension(remoteName).ToLowerInvariant() switch { ".avi" => "avi", ".mkv" or ".webm" => "matroska", _ => "mov" };
            foreach (var arg in new[] { "-http_proxy", "", "-protocol_whitelist", "http,tcp", "-request_size", "65536",
                "-initial_request_size", "65536", "-short_seek_size", "65536", "-multiple_requests", "0", "-seekable", "1",
                "-nofind_stream_info", "-probesize", "32", "-fpsprobesize", "0", "-analyzeduration", "1", "-f", format }) start.ArgumentList.Add(arg);
        }
        foreach (var arg in new[] { "-i", path, "-map", "0:v:0", "-frames:v", "1", "-vf",
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
