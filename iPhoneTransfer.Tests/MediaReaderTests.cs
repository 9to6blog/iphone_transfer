using System.Reflection;
using iMobileDevice.Afc;
using iPhoneTransfer.Core;

internal static class MediaReaderTests
{
    internal static void Run(Action<string> pass)
    {
        var api = DispatchProxy.Create<IAfcApi, FakeAfc>();
        long position = 0;
        int closes = 0;
        bool failSeek = false, failRead = false, emptyRead = false, shortRead = false;
        ((FakeAfc)(object)api).Handler = (method, args) =>
        {
            switch (method.Name)
            {
                case "afc_file_open":
                    if ((AfcFileMode)args[2]! != AfcFileMode.FopenRdonly) throw new Exception("Video preview attempted a write");
                    args[3] = 7UL; return AfcError.Success;
                case "afc_file_seek":
                    if ((int)args[3]! != 0) throw new Exception("Expected absolute seek");
                    position = (long)args[2]!; return failSeek ? AfcError.ReadError : AfcError.Success;
                case "afc_file_read":
                    if (failRead) return AfcError.ReadError;
                    var n = emptyRead ? 0U : shortRead ? Math.Min(7U, (uint)args[3]!) : (uint)args[3]!;
                    for (int i = 0; i < n; i++) ((byte[])args[2]!)[i] = (byte)((position + i) % 251);
                    args[4] = n; return AfcError.Success;
                case "afc_file_close": closes++; return AfcError.Success;
                default: throw new Exception("Unexpected native call " + method.Name);
            }
        };
        IMediaReader NewReader() => (IMediaReader)Activator.CreateInstance(typeof(IPhoneClient).Assembly.GetType("iPhoneTransfer.Core.AfcMediaReader")!,
            BindingFlags.Instance | BindingFlags.NonPublic, null, new object?[] { api, null, "/Documents/movie.mov", 8L << 30 }, null)!;
        void Check(bool value, string message) { if (!value) throw new Exception(message); pass(message); }
        void Fails<T>(Action action, string message) where T : Exception
        { try { action(); throw new Exception("Expected " + typeof(T).Name); } catch (T) { pass(message); } }
        var reader = NewReader();
        var buffer = new byte[1 << 20];
        var offset = (5L << 30) + 13;
        Check(reader.ReadAt(offset, buffer, buffer.Length, default) == 65536 && position == offset && buffer[0] == (byte)(offset % 251),
            "Video preview seeks directly beyond 4 GiB and caps each USB read at 64 KiB");
        shortRead = true;
        Check(reader.ReadAt(100, buffer, 1000, default) == 7 && reader.BytesRead == 65543, "Partial USB reads preserve exact video byte accounting");
        shortRead = false;
        Check(reader.ReadAt(reader.Length, buffer, 1, default) == 0, "Video range EOF performs no USB read");
        failSeek = true;
        Fails<IOException>(() => reader.ReadAt(0, buffer, 10, default), "Failed video seek cannot return data from the wrong position"); failSeek = false;
        failRead = true;
        Fails<IOException>(() => reader.ReadAt(0, buffer, 10, default), "Video USB read errors are not successful thumbnails"); failRead = false;
        emptyRead = true;
        Fails<IOException>(() => reader.ReadAt(0, buffer, 10, default), "Unexpected video EOF fails instead of hanging"); emptyRead = false;
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Fails<OperationCanceledException>(() => reader.ReadAt(0, buffer, 10, cancel.Token), "Canceled video ranges stop before native I/O");
        ((IDisposable)reader).Dispose(); ((IDisposable)reader).Dispose();
        Check(closes == 1, "Video reader closes its native handle exactly once");
        Fails<ObjectDisposedException>(() => reader.ReadAt(0, buffer, 10, default), "Disposed video readers reject further reads");
        var limited = NewReader();
        using ((IDisposable)limited)
        {
            for (int i = 0; i < 256; i++) limited.ReadAt(i * 65536L, buffer, 65536, default);
            Fails<IOException>(() => limited.ReadAt(0, buffer, 1, default), "Video thumbnails stop at 16 MiB instead of downloading the whole movie");
            Check(limited.BytesRead == 16L * 1024 * 1024, "Video byte limit is enforced before the next USB read");
        }
    }
}
