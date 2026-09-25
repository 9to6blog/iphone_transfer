using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ImageMagick;
using System.Buffers.Binary;
using iPhoneTransfer.Core;

namespace iPhoneTransfer.App;

internal static class MediaVerification
{
    internal static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool success, string message) { if (!success) throw new Exception(message); checks.Add(message); }
        using var ct = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        foreach (var format in new[] { MagickFormat.Png, MagickFormat.Jpeg, MagickFormat.Heic })
        {
            using var source = new MagickImage(MagickColors.CornflowerBlue, 120, 240);
            byte[] data;
            if (format == MagickFormat.Heic)
            {
                using var fixture = typeof(MediaVerification).Assembly.GetManifestResourceStream("iPhoneTransfer.TestFixture.heic")!;
                using var buffer = new MemoryStream(); await fixture.CopyToAsync(buffer, ct.Token); data = buffer.ToArray();
            }
            else data = source.ToByteArray(format);
            var bitmap = MediaPreview.DecodeImage(data, 160);
            Check(format == MagickFormat.Heic ? bitmap.PixelWidth == 160 && bitmap.PixelHeight == 107 : bitmap.PixelWidth == 80 && bitmap.PixelHeight == 160,
                $"{format}: thumbnail decodes with correct aspect ratio");
            Check(bitmap.IsFrozen, $"{format}: preview is safe across threads");
            string? copiedPath = null;
            var appBitmap = await MediaPreview.LoadFileAsync("app." + format, 160, async (path, token) =>
            { copiedPath = path; await File.WriteAllBytesAsync(path, data, token); }, ct.Token);
            Check(appBitmap.PixelWidth == bitmap.PixelWidth && appBitmap.IsFrozen && !Directory.Exists(Path.GetDirectoryName(copiedPath)!),
                $"{format}: app preview copy/decode pipeline cleans temporary files");
            var converted = MediaPreview.ConvertToJpeg(data);
            Check(converted is { Length: > 0 } && converted[0] == 0xff && converted[1] == 0xd8, $"{format}: JPEG conversion uses bundled decoder");
            if (format == MagickFormat.Heic)
            {
                var path = Path.Combine(directory, "fixture.heic");
                await File.WriteAllBytesAsync(path, data, ct.Token);
                var full = MediaPreview.DecodeImage(path, 0);
                Check(full.PixelWidth == 1280 && full.PixelHeight == 854, "HEIC: enlarged view retains full dimensions");
            }
        }
        using (var rotated = new MagickImage(MagickColors.Tomato, 120, 240))
        {
            var exif = new ExifProfile(); exif.SetValue(ExifTag.Orientation, (ushort)6); rotated.SetProfile(exif);
            rotated.Orientation = OrientationType.RightTop;
            var bitmap = MediaPreview.DecodeImage(rotated.ToByteArray(MagickFormat.Jpeg), 160);
            Check(bitmap.PixelWidth == 160 && bitmap.PixelHeight == 80, "EXIF portrait orientation is applied before resizing");
        }
        Check(MediaPreview.ConvertToJpeg([1, 2, 3]) == null, "Corrupt image conversion preserves original via null fallback");
        foreach (var codec in new[] { "libx264", "libx265" })
        {
            var path = Path.Combine(directory, codec + ".mov");
            // No faststart: exercise camera movies with the moov atom at the end.
            var start = new ProcessStartInfo(MediaPreview.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=blue:s=120x240:d=0.3", "-c:v", codec, "-threads", "2", "-pix_fmt", "yuv420p", path }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(ct.Token);
            var errors = await error;
            if (process.ExitCode != 0) throw new Exception(errors);
            checks.Add(codec + ": generated synthetic MOV fixture");
            var bitmap = await MediaPreview.VideoFrameAsync(path, 160, ct.Token);
            Check(bitmap.PixelWidth == 80 && bitmap.PixelHeight == 160, codec + ": MOV video thumbnail decodes without Windows codecs");
            var ranges = new PaddedMovieReader(await File.ReadAllBytesAsync(path, ct.Token));
            var appFrame = await MediaPreview.VideoFrameFromReaderAsync(ranges, "app.MOV", 160, ct.Token);
            Check(appFrame.PixelWidth == 80 && appFrame.PixelHeight == 160, codec + ": video thumbnail decodes through random-access ranges");
            Check(ranges.BytesRead < 1024 * 1024 && ranges.ReadTail && ranges.ReadHead && ranges.Length > 500_000_000,
                codec + $": large MOV with trailing metadata reads only {ranges.BytesRead} of {ranges.Length} bytes");
        }
        bool copiedMovie = false;
        try
        {
            await MediaPreview.LoadFileAsync("no-copy.MOV", 160, (_, _) => { copiedMovie = true; return Task.CompletedTask; }, ct.Token);
            throw new Exception("Whole movie copy was accepted");
        }
        catch (InvalidOperationException) { Check(!copiedMovie, "Video thumbnail never enters the full-file copy path"); }
        Check(!VideoRangeServer.TryRange("bytes=0-", long.MaxValue, out _, out _) &&
            !VideoRangeServer.TryRange("bytes=0-999999999", long.MaxValue, out _, out _) &&
            !VideoRangeServer.TryRange("bytes=65536-0", 100000, out _, out _) &&
            VideoRangeServer.TryRange("bytes=65536-131071", 100000, out var rangeStart, out var rangeEnd) && rangeStart == 65536 && rangeEnd == 99999,
            "Range server rejects unbounded/oversized requests and clips at EOF");
        try
        {
            await MediaPreview.VideoFrameFromReaderAsync(new FailedMediaReader(), "error.mov", 160, ct.Token);
            throw new Exception("Broken range source was accepted");
        }
        catch (IOException) { checks.Add("USB range failure stops FFmpeg without falling back to a complete movie copy"); }
        string? failedPath = null;
        try
        {
            await MediaPreview.LoadFileAsync("broken.HEIC", 160, (path, _) =>
            { failedPath = path; File.WriteAllBytes(path, [1, 2, 3]); return Task.CompletedTask; }, ct.Token);
            throw new Exception("Corrupt preview was accepted");
        }
        catch (MagickException) { Check(!Directory.Exists(Path.GetDirectoryName(failedPath)!), "Failed app image decode removes its temporary copy"); }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await MediaPreview.VideoFrameAsync(Path.Combine(directory, "libx265.mov"), 160, canceled.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { checks.Add("Canceled video preview stops its decoder process"); }
            var source = new PaddedMovieReader(await File.ReadAllBytesAsync(Path.Combine(directory, "libx265.mov"), ct.Token));
            try { await MediaPreview.VideoFrameFromReaderAsync(source, "cancel.mov", 160, canceled.Token); throw new Exception("Range cancellation ignored"); }
            catch (OperationCanceledException) { Check(source.BytesRead == 0, "Canceled ranged preview reads no video bytes and closes its server"); }
        }
        var arrivals = new UsbArrivalTracker();
        ProbeResult Present(params string[] ids) => new(ids.Select(id => new iPhoneTransfer.Core.DeviceInfo(id, "fixture", false)).ToList());
        Check(!arrivals.Update(Present()), "No device: no automatic window");
        Check(arrivals.Update(Present("A")), "USB arrival opens window before trust approval");
        Check(!arrivals.Update(Present("A")), "Same device does not repeatedly open window");
        Check(!arrivals.Update(new([], "timeout")) && !arrivals.Update(Present("A")), "Probe errors are not mistaken for reconnects");
        Check(!arrivals.Update(Present()) && arrivals.Update(Present("A")), "Reconnect opens window again");
        Check(arrivals.Update(Present("A", "B")), "Second device arrival is recognized");
        await File.WriteAllTextAsync(Path.Combine(directory, "media-checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class FailedMediaReader : IMediaReader
    {
        public long Length => 1L << 30;
        public long BytesRead => 0;
        public int ReadAt(long offset, byte[] buffer, int count, CancellationToken ct) => throw new IOException("USB fixture failure");
    }

    // A valid MOV with a virtual 512 MiB free atom before its trailing moov metadata.
    // Chunk offsets stay valid; decoding must seek to the tail and back, never traverse the padding.
    private sealed class PaddedMovieReader : IMediaReader
    {
        private readonly byte[] _original;
        private readonly int _moov;
        private const int Padding = 512 * 1024 * 1024;
        private readonly byte[] _free = new byte[8];
        public long Length => _original.Length + (long)Padding;
        public long BytesRead { get; private set; }
        public bool ReadTail { get; private set; }
        public bool ReadHead { get; private set; }
        internal PaddedMovieReader(byte[] original)
        {
            _original = original;
            for (int at = 0; at + 8 <= original.Length;)
            {
                var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(at, 4)));
                if (System.Text.Encoding.ASCII.GetString(original, at + 4, 4) == "moov") { _moov = at; break; }
                if (size < 8) throw new IOException("Invalid fixture atom");
                at += size;
            }
            if (_moov == 0) throw new IOException("Missing trailing moov fixture");
            BinaryPrimitives.WriteInt32BigEndian(_free, Padding);
            System.Text.Encoding.ASCII.GetBytes("free").CopyTo(_free, 4);
        }
        public int ReadAt(long offset, byte[] buffer, int count, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (BytesRead + count > 1024 * 1024) throw new IOException("Decoder tried to read too much of the movie");
            int n = (int)Math.Min(count, Length - offset);
            for (int i = 0; i < n; i++)
            {
                long at = offset + i;
                buffer[i] = at < _moov ? _original[(int)at] : at < _moov + 8 ? _free[(int)(at - _moov)]
                    : at < _moov + (long)Padding ? (byte)0 : _original[(int)(at - Padding)];
            }
            ReadHead |= offset < _moov;
            ReadTail |= offset + n > _moov + (long)Padding;
            BytesRead += n;
            return n;
        }
    }
}
