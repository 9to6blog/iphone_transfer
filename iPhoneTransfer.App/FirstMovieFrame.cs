using System.Buffers.Binary;
using System.IO;
using System.Text;
using iPhoneTransfer.Core;

namespace iPhoneTransfer.App;

/// <summary>Read one independently decodable AVC/HEVC sample without downloading the movie's sample tables.</summary>
internal sealed class FirstMovieFrame
{
    internal sealed record Clip(byte[] Bytes, int SampleBytes, long MetadataBytes);
    internal sealed class Unsupported : IOException { internal Unsupported(string message) : base(message) { } }
    private readonly IMediaReader _source;
    private readonly CancellationToken _ct;
    private readonly Dictionary<long, byte[]> _pages = new();
    private const int PageSize = 1024, MaxMetadataBytes = 256 * 1024;
    private readonly record struct Box(long Start, long Size, int Header, string Type)
    { internal long Body => Start + Header; internal long End => Start + Size; }
    private FirstMovieFrame(IMediaReader source, CancellationToken ct) { _source = source; _ct = ct; }
    internal static Clip Read(IMediaReader source, CancellationToken ct) => new FirstMovieFrame(source, ct).Read();

    private Clip Read()
    {
        var moov = Required(new(0, _source.Length, 0, "root"), "moov");
        foreach (var track in Children(moov).Where(b => b.Type == "trak"))
        {
            var media = Required(track, "mdia");
            var handler = Required(media, "hdlr");
            if (Encoding.ASCII.GetString(Field(handler, 8, 4)) != "vide") continue;
            var table = Required(Required(media, "minf"), "stbl");
            var sizes = Required(table, "stsz");
            if (U32(sizes, 8) == 0) throw new Unsupported("분할 저장된 영상입니다.");
            var sampleSize = U32(sizes, 4);
            if (sampleSize == 0) sampleSize = U32(sizes, 12);
            if (sampleSize == 0 || sampleSize > 8 * 1024 * 1024) throw new Unsupported("첫 프레임 크기가 빠른 미리보기 범위를 넘습니다.");
            var sync = Children(table).FirstOrDefault(b => b.Type == "stss");
            if (sync.Size > 0 && (U32(sync, 4) == 0 || U32(sync, 8) != 1)) throw new Unsupported("첫 프레임을 독립적으로 해독할 수 없습니다.");
            var mapping = Required(table, "stsc");
            if (U32(mapping, 4) == 0 || U32(mapping, 8) != 1 || U32(mapping, 12) == 0) throw new Unsupported("첫 영상 청크 정보가 없습니다.");
            var descriptionId = U32(mapping, 16);
            var description = Required(table, "stsd");
            var descriptions = U32(description, 4);
            if (descriptionId == 0 || descriptionId > descriptions) throw new Unsupported("영상 코덱 정보가 없습니다.");
            var sampleEntry = Children(new(description.Body + 8, description.End - description.Body - 8, 0, "entries"))
                .Skip(checked((int)descriptionId - 1)).FirstOrDefault();
            if (sampleEntry.Type is not ("avc1" or "avc3" or "hvc1" or "hev1")) throw new Unsupported("AVC/HEVC 이외의 영상입니다.");
            if (BinaryPrimitives.ReadUInt16BigEndian(Field(sampleEntry, 6, 2)) != 1) throw new Unsupported("외부 영상 데이터 참조는 지원하지 않습니다.");
            var chunks = Children(table).FirstOrDefault(b => b.Type is "stco" or "co64");
            if (chunks.Size == 0 || U32(chunks, 4) == 0) throw new Unsupported("영상 위치 정보가 없습니다.");
            long offset = chunks.Type == "stco" ? U32(chunks, 8) : checked((long)BinaryPrimitives.ReadUInt64BigEndian(Field(chunks, 8, 8)));
            if (offset < 0 || offset > _source.Length - sampleSize) throw new IOException("첫 프레임 위치가 파일 범위를 벗어납니다.");
            var timing = Required(table, "stts");
            uint delta = U32(timing, 4) > 0 ? Math.Max(1U, U32(timing, 12)) : 1;
            var mvhd = Full(Required(moov, "mvhd")); SetDuration(mvhd, false, 1);
            var tkhd = Full(Required(track, "tkhd")); SetDuration(tkhd, true, 1);
            var mdhd = Full(Required(media, "mdhd")); SetDuration(mdhd, false, delta);
            var hdlr = Full(handler);
            var stsd = Full(description);
            var metadataBytes = _source.BytesRead;
            var sample = ReadExact(offset, checked((int)sampleSize));
            var ftyp = Atom("ftyp", Encoding.ASCII.GetBytes("isom\0\0\0\0isomiso2mp41"));
            var movie = Movie(0);
            movie = Movie((ulong)(ftyp.Length + movie.Length + 8));
            return new(Join(ftyp, movie, Atom("mdat", sample)), sample.Length, metadataBytes);

            byte[] Movie(ulong dataOffset) => Atom("moov", mvhd, Atom("trak", tkhd,
                Atom("mdia", mdhd, hdlr, Atom("minf", Atom("vmhd", Words(1, 0, 0)),
                    Atom("dinf", Atom("dref", Words(0, 1), Atom("url ", Words(1)))),
                    Atom("stbl", stsd, Atom("stts", Words(0, 1, 1, delta)),
                        Atom("stsc", Words(0, 1, 1, 1, descriptionId)), Atom("stsz", Words(0, sampleSize, 1)),
                        Atom("co64", Words(0, 1, (uint)(dataOffset >> 32), (uint)dataOffset)))))));
        }
        throw new Unsupported("영상 트랙을 찾을 수 없습니다.");
    }

    private IEnumerable<Box> Children(Box parent)
    {
        int count = 0;
        for (long at = parent.Body; at < parent.End;)
        {
            _ct.ThrowIfCancellationRequested();
            if (++count > 512 || parent.End - at < 8) throw new IOException("영상 정보 구조가 올바르지 않습니다.");
            var header = Get(at, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            int headerSize = 8;
            if (size == 1) { size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(Get(at + 8, 8))); headerSize = 16; }
            else if (size == 0) size = parent.End - at;
            if (size < headerSize || size > parent.End - at) throw new IOException("영상 정보 크기가 파일 범위를 벗어납니다.");
            yield return new(at, size, headerSize, Encoding.ASCII.GetString(header, 4, 4));
            at += size;
        }
    }
    private Box Required(Box parent, string name) => Children(parent).FirstOrDefault(b => b.Type == name) is { Size: > 0 } box
        ? box : throw new Unsupported("첫 프레임 정보가 없습니다: " + name);
    private byte[] Field(Box box, int at, int count)
    { if (at < 0 || count > box.End - box.Body - at) throw new IOException("영상 정보 필드가 잘렸습니다."); return Get(box.Body + at, count); }
    private uint U32(Box box, int at) => BinaryPrimitives.ReadUInt32BigEndian(Field(box, at, 4));
    private byte[] Full(Box box)
    { if (box.Size > 256 * 1024) throw new Unsupported("영상 코덱 정보가 너무 큽니다."); return Get(box.Start, (int)box.Size); }

    private byte[] Get(long offset, int size)
    {
        if (offset < 0 || size < 0 || offset > _source.Length - size) throw new IOException("영상 정보 범위가 올바르지 않습니다.");
        var result = new byte[size];
        int done = 0;
        while (done < size)
        {
            long pageStart = (offset + done) / PageSize * PageSize;
            if (!_pages.TryGetValue(pageStart, out var page))
            {
                if (_pages.Count >= MaxMetadataBytes / PageSize) throw new Unsupported("첫 프레임 정보 탐색 한도에 도달했습니다.");
                page = ReadExact(pageStart, (int)Math.Min(PageSize, _source.Length - pageStart));
                _pages.Add(pageStart, page);
            }
            int within = (int)(offset + done - pageStart);
            int n = Math.Min(size - done, page.Length - within);
            page.AsSpan(within, n).CopyTo(result.AsSpan(done)); done += n;
        }
        return result;
    }
    private byte[] ReadExact(long offset, int size)
    {
        var result = new byte[size]; var buffer = new byte[Math.Min(65536, size)];
        int done = 0;
        while (done < size)
        {
            _ct.ThrowIfCancellationRequested();
            int n = _source.ReadAt(offset + done, buffer, Math.Min(buffer.Length, size - done), _ct);
            if (n <= 0 || n > size - done) throw new IOException("첫 프레임 읽기가 중단되었습니다.");
            buffer.AsSpan(0, n).CopyTo(result.AsSpan(done)); done += n;
        }
        return result;
    }
    private static void SetDuration(byte[] atom, bool track, uint duration)
    {
        int header = BinaryPrimitives.ReadUInt32BigEndian(atom) == 1 ? 16 : 8;
        if (atom.Length <= header) throw new IOException("영상 시간 정보가 잘렸습니다.");
        int version = atom[header];
        int at = header + (version == 1 ? (track ? 28 : 24) : (track ? 20 : 16));
        if (version is not (0 or 1) || at + (version == 1 ? 8 : 4) > atom.Length) throw new IOException("영상 시간 정보가 올바르지 않습니다.");
        if (version == 1) BinaryPrimitives.WriteUInt64BigEndian(atom.AsSpan(at), duration);
        else BinaryPrimitives.WriteUInt32BigEndian(atom.AsSpan(at), duration);
    }
    private static byte[] Words(params uint[] values)
    { var bytes = new byte[values.Length * 4]; for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), values[i]); return bytes; }
    private static byte[] Join(params byte[][] parts)
    { var result = new byte[parts.Sum(p => p.Length)]; int at = 0; foreach (var part in parts) { part.CopyTo(result, at); at += part.Length; } return result; }
    private static byte[] Atom(string type, params byte[][] bodies)
    {
        var body = Join(bodies); var result = new byte[body.Length + 8];
        BinaryPrimitives.WriteInt32BigEndian(result, result.Length); Encoding.ASCII.GetBytes(type).CopyTo(result, 4); body.CopyTo(result, 8); return result;
    }
}
