using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using iPhoneTransfer.Core;

namespace iPhoneTransfer.App;

/// <summary>A short-lived loopback source for FFmpeg's bounded HTTP range requests.</summary>
internal sealed class VideoRangeServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop;
    private readonly CancellationTokenSource _failed = new();
    private readonly IMediaReader _source;
    private readonly string _path = "/" + Guid.NewGuid().ToString("N");
    private readonly Task _worker;
    internal string Url { get; }
    internal Exception? Error { get; private set; }
    internal CancellationToken Failure => _failed.Token;

    internal VideoRangeServer(IMediaReader source, CancellationToken ct)
    {
        _source = source;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{_path}";
        _worker = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                client.NoDelay = true;
                using var stream = client.GetStream();
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var ct = requestTimeout.Token;
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var request = await reader.ReadLineAsync(ct);
                string? range = null;
                var headerBytes = request?.Length ?? 0;
                for (;;)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (string.IsNullOrEmpty(line)) break;
                    headerBytes += line.Length;
                    if (headerBytes > 8192) throw new IOException("영상 미리보기 요청이 너무 큽니다.");
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line[6..].Trim();
                }
                var parts = request?.Split(' ');
                if (parts?.Length != 3 || parts[1] != _path || parts[0] is not ("GET" or "HEAD"))
                { await Header("404 Not Found", 0, null); continue; }
                if (parts[0] == "HEAD") { await Header("200 OK", _source.Length, null); continue; }
                if (!TryRange(range, _source.Length, out var start, out var end))
                { await Header("416 Range Not Satisfiable", 0, $"bytes */{_source.Length}"); continue; }
                await Header("206 Partial Content", end - start + 1, $"bytes {start}-{end}/{_source.Length}");
                var buffer = new byte[64 * 1024];
                for (var offset = start; offset <= end;)
                {
                    ct.ThrowIfCancellationRequested();
                    var read = _source.ReadAt(offset, buffer, (int)Math.Min(buffer.Length, end - offset + 1), ct);
                    if (read <= 0) throw new IOException("영상 부분 읽기가 끝나기 전에 연결이 종료되었습니다.");
                    offset += read;
                    try { await stream.WriteAsync(buffer.AsMemory(0, read), ct); }
                    catch (IOException) { break; } // FFmpeg may abandon a range as soon as a frame is decoded.
                }

                async Task Header(string status, long length, string? contentRange)
                {
                    var response = $"HTTP/1.1 {status}\r\nConnection: close\r\nAccept-Ranges: bytes\r\nContent-Type: application/octet-stream\r\nContent-Length: {length}\r\n";
                    if (contentRange != null) response += $"Content-Range: {contentRange}\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "\r\n"), ct);
                }
            }
        }
        catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or IOException) { }
        catch (Exception ex) { Error = ex; _failed.Cancel(); }
    }

    internal static bool TryRange(string? range, long length, out long start, out long end)
    {
        start = end = 0;
        if (length <= 0 || range == null || !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = range[6..].Split('-');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) || start >= length || end < start) return false;
        end = Math.Min(end, length - 1);
        return end - start < 64 * 1024; // Never accept an unbounded/full-file request.
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        await _worker;
        _stop.Dispose(); _failed.Dispose();
    }
}
