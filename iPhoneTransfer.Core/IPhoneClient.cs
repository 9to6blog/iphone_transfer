using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using iMobileDevice;
using iMobileDevice.Afc;
using iMobileDevice.HouseArrest;
using iMobileDevice.iDevice;
using iMobileDevice.InstallationProxy;
using iMobileDevice.Lockdown;
using iMobileDevice.Plist;

namespace iPhoneTransfer.Core;

/// <summary>
/// libimobiledevice 위에 올린 고수준 API.
/// - 아이폰 → PC: DCIM 사진/영상 목록 조회 및 복사
/// - PC → 아이폰: 파일 공유 지원 앱 목록 조회 후, 선택한 앱의 Documents 폴더로 전송
/// 모든 공개 메서드는 백그라운드 스레드에서 동작하도록 Task 로 감싼다.
/// </summary>
public static partial class IPhoneClient
{
    private const string Label = "iPhoneTransfer";

    // 가져올 미디어 확장자(필요 시 추가)
    private static readonly HashSet<string> MediaExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".tiff", ".webp",
        ".mov", ".mp4", ".m4v", ".avi", ".dng"
    };

    // 이미지 → JPG 변환에서 제외할 동영상 확장자.
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    { ".mov", ".mp4", ".m4v", ".avi" };

    private static bool IsVideo(string name) => VideoExt.Contains(Path.GetExtension(name));

    private static bool IsJpeg(string name)
    {
        var e = Path.GetExtension(name).ToLowerInvariant();
        return e is ".jpg" or ".jpeg";
    }

    private static readonly object InitLock = new();
    private static bool _initialized;

    private static ILibiMobileDevice Lib => LibiMobileDevice.Instance;

    /// <summary>네이티브 libimobiledevice DLL을 1회 로드한다. 모든 작업 전에 호출됨.</summary>
    public static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_initialized) return;
            NativeLibraries.Load();
            if (!NativeLibraries.LibraryFound)
                throw new MobileDeviceException(
                    "네이티브 libimobiledevice 라이브러리를 찾지 못했습니다. " +
                    "프로그램 폴더에 native DLL이 함께 있는지 확인하세요.");
            _initialized = true;
        }
    }

    // ───────────────────────── 기기 목록 / 이름 ─────────────────────────

    /// <summary>현재 USB로 연결된(페어링 가능한) 기기 목록.</summary>
    public static IReadOnlyList<DeviceInfo> ListDevices()
    {
        EnsureInitialized();
        int count = 0;
        var err = Lib.iDevice.idevice_get_device_list(out ReadOnlyCollection<string> udids, ref count);
        Check(err, "Apple USB 통신에 실패했습니다. 연결 센터에서 드라이버와 서비스를 확인하세요");
        if (udids == null) return Array.Empty<DeviceInfo>();

        var list = new List<DeviceInfo>();
        foreach (var udid in udids.Distinct())
        {
            try
            {
                var name = GetDeviceName(udid);
                using var device = OpenDevice(udid);
                var error = Lib.Afc.afc_client_start_service(device, out var afc, Label);
                using (afc)
                {
                    list.Add(new DeviceInfo(udid, name, error == AfcError.Success,
                        error == AfcError.Success ? null : $"파일 접근 대기 [{error}] — 잠금을 해제하고 다시 연결하세요."));
                }
            }
            catch (MobileDeviceException ex)
            {
                list.Add(new DeviceInfo(udid, "iPhone · 연결 확인 필요", false, ex.Message));
            }
        }
        return list;
    }

    private static string GetDeviceName(string udid)
    {
        using var device = OpenDevice(udid);
        Check(Lib.Lockdown.lockdownd_client_new_with_handshake(device, out var client, Label),
            "기기 페어링 실패 — 아이폰 화면에서 '이 컴퓨터를 신뢰'를 눌러야 합니다");
        using (client)
        {
            Lib.Lockdown.lockdownd_get_device_name(client, out string name);
            return string.IsNullOrWhiteSpace(name) ? udid : name;
        }
    }

    private static iDeviceHandle OpenDevice(string udid)
    {
        EnsureInitialized();
        Check(Lib.iDevice.idevice_new(out var device, udid),
            "기기에 연결할 수 없습니다 (USB 연결/드라이버 확인)");
        return device;
    }

    // ───────────────────────── 사진 가져오기 (iPhone → PC) ─────────────────────────

    /// <summary>Pairing-free USB detection for the background connection watcher.</summary>
    public static IReadOnlyList<string> ListUsbDeviceIds()
    {
        EnsureInitialized();
        int count = 0;
        Check(Lib.iDevice.idevice_get_device_list(out ReadOnlyCollection<string> ids, ref count), "Apple USB 검색 실패");
        return ids?.Distinct().ToArray() ?? Array.Empty<string>();
    }

    /// <summary>Stream even large camera videos to disk, never truncate or buffer the entire movie.</summary>
    public static Task CopyPhotoToFileAsync(string udid, PhotoItem item, string destination, CancellationToken ct)
        => Task.Run(() =>
        {
            using var device = OpenDevice(udid);
            var api = Lib.Afc;
            Check(api.afc_client_start_service(device, out var afc, Label), "사진 영역(AFC) 접근 실패");
            using (afc)
            {
                try
                {
                    ReadDeviceFile(api, afc, item.DevicePath, destination, ct);
                    ct.ThrowIfCancellationRequested();
                    if (new FileInfo(destination).Length != item.Size) throw new IOException("원본 크기가 달라졌습니다. 사진 목록을 다시 불러오세요.");
                }
                catch { try { File.Delete(destination); } catch { } throw; }
            }
        }, ct);

    public static Task<List<PhotoItem>> ListPhotosAsync(string udid, CancellationToken ct = default)
        => Task.Run(() => ListPhotos(udid, ct), ct);

    private static List<PhotoItem> ListPhotos(string udid, CancellationToken ct)
    {
        var afcApi = Lib.Afc;
        using var device = OpenDevice(udid);
        Check(afcApi.afc_client_start_service(device, out var afc, Label),
            "사진 영역(AFC) 접근 실패 — 기기 잠금 해제 후 다시 시도하세요");
        using (afc)
        {
            var photos = new List<PhotoItem>();
            WalkDir(afcApi, afc, "/DCIM", photos, ct);
            // 최신순(촬영/수정 날짜 내림차순). 날짜가 같으면 파일명 보조 정렬.
            return photos
                .OrderByDescending(p => p.Modified)
                .ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static void WalkDir(IAfcApi api, AfcClientHandle afc, string dir, List<PhotoItem> outList, CancellationToken ct)
    {
        Check(api.afc_read_directory(afc, dir, out ReadOnlyCollection<string> entries),
            "사진 폴더를 읽지 못했습니다. 잠금 해제와 USB 연결을 확인하세요");
        if (entries == null) throw new MobileDeviceException("사진 폴더 응답이 비어 있습니다. 다시 연결하세요.");

        foreach (var name in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (name is "." or "..") continue;
            var path = dir.TrimEnd('/') + "/" + name;
            var (isDir, size, modified) = GetInfo(api, afc, path);
            if (isDir)
                WalkDir(api, afc, path, outList, ct);
            else if (MediaExt.Contains(Path.GetExtension(name)))
                outList.Add(new PhotoItem(path, name, size, modified));
        }
    }

    private static (bool IsDir, long Size, DateTime Modified) GetInfo(IAfcApi api, AfcClientHandle afc, string path)
    {
        Check(api.afc_get_file_info(afc, path, out ReadOnlyCollection<string> kv), "파일 정보 조회 실패");
        if (kv == null) throw new MobileDeviceException("파일 정보 응답이 없습니다.");

        long size = 0;
        bool isDir = false;
        DateTime mtime = default, birth = default;
        for (int i = 0; i + 1 < kv.Count; i += 2)
        {
            switch (kv[i])
            {
                case "st_size": long.TryParse(kv[i + 1], out size); break;
                case "st_ifmt": isDir = kv[i + 1] == "S_IFDIR"; break;
                // AFC는 시각을 Unix epoch 기준 나노초 문자열로 준다.
                case "st_mtime": mtime = FromAfcTime(kv[i + 1], mtime); break;
                case "st_birthtime": birth = FromAfcTime(kv[i + 1], birth); break;
            }
        }
        // 촬영(캡쳐) 시각에 가장 가까운 값: 생성시각(birthtime) 우선, 없으면 수정시각(mtime).
        var modified = birth != default ? birth : mtime;
        return (isDir, size, modified);
    }

    private static DateTime FromAfcTime(string nanos, DateTime fallback)
    {
        if (long.TryParse(nanos, out var ns) && ns > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(ns / 1_000_000).LocalDateTime;
        return fallback;
    }

    public static Task ImportPhotosAsync(string udid, IReadOnlyList<PhotoItem> items, string destFolder,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default,
        Func<byte[], byte[]?>? jpegConverter = null)
        => Task.Run(() => ImportPhotos(udid, items, destFolder, progress, ct, jpegConverter), ct);

    private static void ImportPhotos(string udid, IReadOnlyList<PhotoItem> items, string destFolder,
        IProgress<TransferProgress>? progress, CancellationToken ct, Func<byte[], byte[]?>? jpegConverter)
    {
        var api = Lib.Afc;
        using var device = OpenDevice(udid);
        Check(api.afc_client_start_service(device, out var afc, Label), "사진 영역(AFC) 접근 실패");
        using (afc)
        {
            Directory.CreateDirectory(destFolder);
            long totalBytes = items.Sum(i => i.Size);
            long doneBytes = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int idx = 0;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                int curIdx = ++idx;
                progress?.Report(new TransferProgress(curIdx, items.Count, item.FileName, doneBytes, totalBytes));

                // 사진은 JPG로 변환, 동영상·이미 JPG인 파일은 원본 그대로 가져온다.
                bool convert = jpegConverter != null && !IsVideo(item.FileName) && !IsJpeg(item.FileName);
                string? dest = null;
                string? partial = Path.Combine(destFolder, $".iPhoneTransfer-{Guid.NewGuid():N}.partial");
                try
                {
                    if (convert)
                    {
                        var raw = ReadOpenFile(api, afc, item.DevicePath, ct);
                        if (raw.LongLength != item.Size) throw new IOException("원본 크기와 읽은 크기가 다릅니다. 다시 연결 후 목록을 불러오세요.");
                        var jpg = jpegConverter!(raw);
                        if (jpg != null)
                        {
                            dest = UniquePath(Path.Combine(destFolder, Path.GetFileNameWithoutExtension(item.FileName) + ".jpg"));
                            File.WriteAllBytes(partial, jpg);
                        }
                        else
                        {
                            // 변환 실패(코덱 미설치 등) → 원본 그대로 저장해 사진을 잃지 않는다.
                            dest = UniquePath(Path.Combine(destFolder, item.FileName));
                            File.WriteAllBytes(partial, raw);
                        }
                        doneBytes += item.Size;
                        progress?.Report(new TransferProgress(curIdx, items.Count, Path.GetFileName(dest), doneBytes, totalBytes));
                    }
                    else
                    {
                        dest = UniquePath(Path.Combine(destFolder, item.FileName));
                        ReadDeviceFile(api, afc, item.DevicePath, partial, ct, delta =>
                        {
                            doneBytes += delta;
                            if (sw.ElapsedMilliseconds >= 100)
                            {
                                progress?.Report(new TransferProgress(curIdx, items.Count, item.FileName, doneBytes, totalBytes));
                                sw.Restart();
                            }
                        });
                        if (new FileInfo(partial).Length != item.Size) throw new IOException("전송한 파일 크기가 원본과 다릅니다. 다시 시도하세요.");
                    }
                    ct.ThrowIfCancellationRequested();
                    File.Move(partial, dest!); // No overwrite; publish only a complete file.
                    partial = null;
                }
                catch
                {
                    // 취소 시 절반만 쓰인 파일을 남기지 않는다.
                    try { if (partial != null && File.Exists(partial)) File.Delete(partial); } catch { /* 무시 */ }
                    throw;
                }
                // 원본 촬영/수정 날짜를 보존(탐색기 날짜순 정렬 유지).
                if (dest != null && item.Modified != default)
                {
                    try { File.SetLastWriteTime(dest, item.Modified); File.SetCreationTime(dest, item.Modified); }
                    catch { /* 날짜 보존 실패는 치명적이지 않음 */ }
                }
            }
            progress?.Report(new TransferProgress(items.Count, items.Count, "", totalBytes, totalBytes));
        }
    }

    private static void ReadDeviceFile(IAfcApi api, AfcClientHandle afc, string devPath, string destPath,
        CancellationToken ct, Action<long>? onBytes = null)
    {
        ulong h = 0;
        Check(api.afc_file_open(afc, devPath, AfcFileMode.FopenRdonly, ref h), $"기기 파일 열기 실패: {devPath}");
        try
        {
            using var fs = File.Create(destPath);
            byte[] buf = new byte[1 << 20];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                uint read = 0;
                var e = api.afc_file_read(afc, h, buf, (uint)buf.Length, ref read);
                Check(e, "파일 읽기가 중단되었습니다. USB 연결을 확인한 후 다시 시도하세요");
                if (read == 0) break;
                fs.Write(buf, 0, (int)read);
                onBytes?.Invoke(read);
            }
        }
        finally { api.afc_file_close(afc, h); }
    }

    // ───────────────────────── 공유 앱 목록 (PC → iPhone 1단계) ─────────────────────────

    /// <summary>파일 공유(UIFileSharingEnabled)를 지원하는 설치 앱 목록을 비동기로 조회.</summary>
    public static Task<List<SharingApp>> ListSharingAppsAsync(string udid, CancellationToken ct = default)
        => Task.Run(() => ListSharingApps(udid, ct), ct);

    private static List<SharingApp> ListSharingApps(string udid, CancellationToken ct)
    {
        var ip = Lib.InstallationProxy;
        var plist = Lib.Plist;
        using var device = OpenDevice(udid);
        Check(ip.instproxy_client_start_service(device, out var client, Label), "앱 목록 서비스 시작 실패");
        using (client)
        {
            using var opts = ip.instproxy_client_options_new();
            ip.instproxy_client_options_add(opts, "ApplicationType", "User", 0);
            var err = ip.instproxy_browse(client, opts, out PlistHandle result);
            // PlistHandle owns this plist. Dispose it once; calling the native free
            // as well leaves a live SafeHandle which later double-frees the native heap.
            Check(err, "앱 목록 조회 실패");

            using (result)
            {
                // plist 전체를 XML로 직렬화한 뒤 관리코드에서 파싱한다.
                // (자식 노드 핸들을 직접 다루면 borrowed 핸들이 잘못 해제되어 네이티브 힙이
                //  깨지고 이후 작업에서 크래시가 나므로, 이 방식이 가장 안전하다.)
                uint len = 0;
                plist.plist_to_xml(result, out string xml, ref len);
                return ParseSharingApps(xml, ct);
            }
        }
    }

    // ───────────────────────── 앱으로 파일 전송 (PC → iPhone 2단계) ─────────────────────────

    public static Task SendFilesToAppAsync(string udid, string bundleId, IReadOnlyList<string> files,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => SendFilesToApp(udid, bundleId, files, progress, ct), ct);

    private static void SendFilesToApp(string udid, string bundleId, IReadOnlyList<string> files,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var ha = Lib.HouseArrest;
        var afcApi = Lib.Afc;
        using var device = OpenDevice(udid);
        Check(ha.house_arrest_client_start_service(device, out var haClient, Label), "앱 컨테이너 접근 실패");
        using (haClient)
        {
            Check(ha.house_arrest_send_command(haClient, "VendDocuments", bundleId), "VendDocuments 명령 실패");
            Check(ha.house_arrest_get_result(haClient, out var resp), "앱 컨테이너 응답 없음");
            resp?.Dispose();

            var afcErr = ha.afc_client_new_from_house_arrest_client(haClient, out var afc);
            if (afcErr != AfcError.Success)
                throw new MobileDeviceException(
                    $"앱 파일 영역 연결 실패 ({afcErr}). 이 앱이 파일 공유를 지원하지 않거나 잠겨 있을 수 있습니다.");
            using (afc)
            {
                long totalBytes = 0;
                foreach (var f in files) { try { totalBytes += new FileInfo(f).Length; } catch { /* 접근 불가 파일은 0 처리 */ } }
                long doneBytes = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int idx = 0;
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    int curIdx = ++idx;
                    var name = Path.GetFileName(f);
                    progress?.Report(new TransferProgress(curIdx, files.Count, name, doneBytes, totalBytes));
                    var destination = UniqueDevicePath(afcApi, afc, "/Documents/" + name);
                    var partial = "/Documents/.iPhoneTransfer-" + Guid.NewGuid().ToString("N") + ".partial";
                    try
                    {
                        WriteDeviceFile(afcApi, afc, partial, f, ct, delta =>
                        {
                            doneBytes += delta;
                            if (sw.ElapsedMilliseconds >= 100)
                            {
                                progress?.Report(new TransferProgress(curIdx, files.Count, name, doneBytes, totalBytes));
                                sw.Restart();
                            }
                        });
                        ct.ThrowIfCancellationRequested();
                        if (GetInfo(afcApi, afc, partial).Size != new FileInfo(f).Length)
                            throw new IOException("아이폰에 기록한 파일 크기가 원본과 다릅니다.");
                        destination = UniqueDevicePath(afcApi, afc, destination);
                        Check(afcApi.afc_rename_path(afc, partial, destination), "전송 파일 확정 실패");
                    }
                    catch { afcApi.afc_remove_path(afc, partial); throw; }
                }
                progress?.Report(new TransferProgress(files.Count, files.Count, "", totalBytes, totalBytes));
            }
        }
    }

    private static void WriteDeviceFile(IAfcApi api, AfcClientHandle afc, string devPath, string srcPath,
        CancellationToken ct, Action<long>? onBytes = null)
    {
        ulong h = 0;
        Check(api.afc_file_open(afc, devPath, AfcFileMode.FopenWronly, ref h), $"기기에 파일 생성 실패: {devPath}");
        try
        {
            using var fs = File.OpenRead(srcPath);
            byte[] buf = new byte[1 << 20];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int total = 0;
                while (total < n)
                {
                    ct.ThrowIfCancellationRequested();
                    byte[] piece = total == 0 ? buf : buf[total..];
                    uint written = 0;
                    var e = api.afc_file_write(afc, h, piece, (uint)(n - total), ref written);
                    if (e != AfcError.Success) throw new MobileDeviceException($"기기 쓰기 실패: {e}");
                    if (written == 0) throw new MobileDeviceException("기기 쓰기가 0바이트로 중단되었습니다");
                    total += (int)written;
                }
                onBytes?.Invoke(n);
            }
        }
        finally { api.afc_file_close(afc, h); }
    }

    // ───────────────────────── plist(XML) 파싱 ─────────────────────────

    private static List<SharingApp> ParseSharingApps(string xml, CancellationToken ct)
    {
        var apps = new List<SharingApp>();
        if (string.IsNullOrEmpty(xml)) return apps;

        // Apple plist XML 은 외부 DTD(DOCTYPE)를 참조하므로 DTD 처리를 무시하고 읽는다.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var doc = XDocument.Load(reader);

        var array = doc.Root?.Element("array");
        if (array == null) return apps;

        foreach (var dict in array.Elements("dict"))
        {
            ct.ThrowIfCancellationRequested();
            var map = ReadPlistDict(dict);
            if (!map.TryGetValue("UIFileSharingEnabled", out var fs) || fs != "true") continue;
            if (!map.TryGetValue("CFBundleIdentifier", out var bid) || string.IsNullOrEmpty(bid)) continue;

            var disp = map.TryGetValue("CFBundleDisplayName", out var d1) && !string.IsNullOrEmpty(d1) ? d1
                     : map.TryGetValue("CFBundleName", out var d2) && !string.IsNullOrEmpty(d2) ? d2
                     : bid;
            apps.Add(new SharingApp(bid, disp));
        }
        return apps.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>plist &lt;dict&gt; 는 &lt;key&gt; 다음에 값 요소가 번갈아 나온다. 평탄한 맵으로 변환.</summary>
    private static Dictionary<string, string> ReadPlistDict(XElement dict)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var nodes = dict.Elements().ToList();
        for (int i = 0; i + 1 < nodes.Count; i += 2)
        {
            if (nodes[i].Name.LocalName != "key") continue;
            var key = nodes[i].Value;
            var val = nodes[i + 1];
            map[key] = val.Name.LocalName switch
            {
                "true" => "true",
                "false" => "false",
                _ => val.Value
            };
        }
        return map;
    }

    // ───────────────────────── 미리보기용 파일 읽기 ─────────────────────────

    /// <summary>기기의 파일 1개를 메모리로 읽어온다(미리보기 등). maxBytes&gt;0이면 그 크기에서 멈춤.</summary>
    public static Task<byte[]> ReadFileBytesAsync(string udid, string devicePath, long maxBytes = 0, CancellationToken ct = default)
        => Task.Run(() => ReadFileBytes(udid, devicePath, maxBytes, ct), ct);

    private static byte[] ReadFileBytes(string udid, string devicePath, long maxBytes, CancellationToken ct)
    {
        var api = Lib.Afc;
        using var device = OpenDevice(udid);
        Check(api.afc_client_start_service(device, out var afc, Label), "사진 영역(AFC) 접근 실패");
        using (afc)
        {
            ulong h = 0;
            Check(api.afc_file_open(afc, devicePath, AfcFileMode.FopenRdonly, ref h), $"기기 파일 열기 실패: {devicePath}");
            try
            {
                using var ms = new MemoryStream();
                byte[] buf = new byte[1 << 20];
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    uint read = 0;
                    var e = api.afc_file_read(afc, h, buf, (uint)buf.Length, ref read);
                    Check(e, "파일 읽기 실패");
                    if (read == 0) break;
                    ms.Write(buf, 0, (int)read);
                    if (maxBytes > 0 && ms.Length >= maxBytes) break;
                }
                return ms.ToArray();
            }
            finally { api.afc_file_close(afc, h); }
        }
    }

    /// <summary>
    /// AFC 연결 1개를 재사용해 여러 파일을 순차로 읽고, 읽을 때마다 콜백을 호출한다.
    /// (썸네일 대량 로딩처럼 파일이 많을 때 매번 연결을 새로 여는 비용을 피한다.)
    /// 콜백은 백그라운드 스레드에서 호출되며, 한 파일이 실패해도 다음으로 계속 진행한다.
    /// </summary>
    public static Task ReadFilesAsync(string udid, IReadOnlyList<string> devicePaths,
        Action<string, byte[]> onFile, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var api = Lib.Afc;
            using var device = OpenDevice(udid);
            Check(api.afc_client_start_service(device, out var afc, Label), "사진 영역(AFC) 접근 실패");
            using (afc)
            {
                foreach (var path in devicePaths)
                {
                    ct.ThrowIfCancellationRequested();
                    byte[] data;
                    try { data = ReadOpenFile(api, afc, path, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch { onFile(path, Array.Empty<byte>()); continue; }
                    onFile(path, data);
                }
            }
        }, ct);

    private static byte[] ReadOpenFile(IAfcApi api, AfcClientHandle afc, string devPath, CancellationToken ct)
    {
        ulong h = 0;
        Check(api.afc_file_open(afc, devPath, AfcFileMode.FopenRdonly, ref h), $"기기 파일 열기 실패: {devPath}");
        try
        {
            using var ms = new MemoryStream();
            byte[] buf = new byte[1 << 20];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                uint read = 0;
                var e = api.afc_file_read(afc, h, buf, (uint)buf.Length, ref read);
                Check(e, "파일 읽기 실패");
                if (read == 0) break;
                ms.Write(buf, 0, (int)read);
            }
            return ms.ToArray();
        }
        finally { api.afc_file_close(afc, h); }
    }

    // ───────────────────────── 유틸 ─────────────────────────

    private static string UniqueDevicePath(IAfcApi api, AfcClientHandle afc, string path)
    {
        var candidate = path;
        for (int i = 1; ; i++)
        {
            var result = api.afc_get_file_info(afc, candidate, out _);
            if (result == AfcError.ObjectNotFound) return candidate;
            Check(result, "대상 파일 확인 실패");
            candidate = "/Documents/" + Path.GetFileNameWithoutExtension(path) + $" ({i})" + Path.GetExtension(path);
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void Check(iDeviceError e, string msg)
    { if (e != iDeviceError.Success) throw new MobileDeviceException($"{msg} [{e}]"); }

    private static void Check(LockdownError e, string msg)
    { if (e != LockdownError.Success) throw new MobileDeviceException($"{msg} [{e}]"); }

    private static void Check(AfcError e, string msg)
    { if (e != AfcError.Success) throw new MobileDeviceException($"{msg} [{e}]"); }

    private static void Check(InstallationProxyError e, string msg)
    { if (e != InstallationProxyError.Success) throw new MobileDeviceException($"{msg} [{e}]"); }

    private static void Check(HouseArrestError e, string msg)
    { if (e != HouseArrestError.Success) throw new MobileDeviceException($"{msg} [{e}]"); }
}
