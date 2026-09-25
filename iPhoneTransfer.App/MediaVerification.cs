using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ImageMagick;

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
            string? copiedVideo = null;
            var appFrame = await MediaPreview.LoadFileAsync("app.MOV", 160, (destination, token) =>
            { token.ThrowIfCancellationRequested(); copiedVideo = destination; File.Copy(path, destination); return Task.CompletedTask; }, ct.Token);
            Check(appFrame.PixelWidth == 80 && appFrame.PixelHeight == 160 && !Directory.Exists(Path.GetDirectoryName(copiedVideo)!),
                codec + ": app video preview pipeline decodes and removes its temporary movie");
        }
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
}
