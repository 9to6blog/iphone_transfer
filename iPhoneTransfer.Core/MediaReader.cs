using iMobileDevice.Afc;

namespace iPhoneTransfer.Core;

/// <summary>Read-only random access for video metadata and one frame; never a full-file copy.</summary>
public interface IMediaReader
{
    long Length { get; }
    long BytesRead { get; }
    int ReadAt(long offset, byte[] buffer, int count, CancellationToken ct);
}

public static partial class IPhoneClient
{
    public static Task<T> WithMediaReaderAsync<T>(string udid, string? bundleId, PhotoItem item,
        Func<IMediaReader, Task<T>> action, CancellationToken ct)
        => Task.Run(() =>
        {
            if (bundleId != null)
                return WithAppDocuments(udid, bundleId, ct, (api, afc) =>
                {
                    EnsureAppParents(api, afc, item.DevicePath);
                    var current = ReadAppEntry(api, afc, item.DevicePath);
                    if (current.IsDirectory || current.IsSymbolicLink || current.Size != item.Size)
                        throw new IOException("영상 원본이 변경되었습니다. 앱 목록을 다시 불러오세요.");
                    return Read(api, afc);
                });
            if (!item.DevicePath.StartsWith("/DCIM/", StringComparison.Ordinal) || item.DevicePath.Split('/').Any(p => p is "." or ".."))
                throw new ArgumentException("사진 보관함 영상 경로가 올바르지 않습니다.");
            using var device = OpenDevice(udid);
            var api = Lib.Afc;
            Check(api.afc_client_start_service(device, out var afc, Label), "사진 영역(AFC) 접근 실패");
            using (afc)
            {
                var current = GetInfo(api, afc, item.DevicePath);
                if (current.IsDir || current.Size != item.Size) throw new IOException("영상 원본이 변경되었습니다. 사진 목록을 다시 불러오세요.");
                return Read(api, afc);
            }

            T Read(IAfcApi api, AfcClientHandle afc)
            {
                ct.ThrowIfCancellationRequested();
                using var reader = new AfcMediaReader(api, afc, item.DevicePath, item.Size);
                // Keep borrowed AFC/House Arrest handles alive until the decoder has closed its range server.
                return action(reader).GetAwaiter().GetResult();
            }
        }, ct);
}

internal sealed class AfcMediaReader : IMediaReader, IDisposable
{
    internal const long ReadLimit = 16L * 1024 * 1024;
    private readonly IAfcApi _api;
    private readonly AfcClientHandle _afc;
    private ulong _file;
    private bool _disposed;
    public long Length { get; }
    public long BytesRead { get; private set; }

    internal AfcMediaReader(IAfcApi api, AfcClientHandle afc, string path, long length)
    {
        if (length <= 0) throw new IOException("비어 있는 영상은 미리볼 수 없습니다.");
        _api = api; _afc = afc; Length = length;
        Check(api.afc_file_open(afc, path, AfcFileMode.FopenRdonly, ref _file));
    }

    public int ReadAt(long offset, byte[] buffer, int count, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (offset < 0 || offset > Length || count < 0 || count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var requested = (int)Math.Min(Math.Min(count, 64 * 1024), Length - offset);
        if (requested == 0) return 0;
        if (BytesRead + requested > ReadLimit)
            throw new IOException("썸네일 부분 읽기 한도(16 MB)에 도달했습니다. 전체 영상은 자동 다운로드하지 않습니다.");
        Check(_api.afc_file_seek(_afc, _file, offset, 0)); // SEEK_SET, including metadata at the end of a large MOV.
        uint read = 0;
        Check(_api.afc_file_read(_afc, _file, buffer, (uint)requested, ref read));
        if (read == 0 || read > requested) throw new IOException("영상 일부를 읽지 못했습니다. USB 연결을 확인하세요.");
        BytesRead += read;
        ct.ThrowIfCancellationRequested();
        return (int)read;
    }

    private static void Check(AfcError error)
    { if (error != AfcError.Success) throw new IOException($"영상 부분 읽기 실패: {error}"); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _api.afc_file_close(_afc, _file);
    }
}
