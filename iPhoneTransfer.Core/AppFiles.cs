using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using iMobileDevice.Afc;

namespace iPhoneTransfer.Core;

public static partial class IPhoneClient
{
    public static Task<List<AppFileItem>> ListAppFilesAsync(string udid, string bundleId,
        string directory = "/Documents", CancellationToken ct = default)
        => Task.Run(() => WithAppDocuments(udid, bundleId, ct,
            (api, afc) => ReadAppDirectory(api, afc, directory, ct)), ct);

    public static Task<AppImportResult> ImportAppFilesAsync(string udid, string bundleId,
        IReadOnlyList<string> devicePaths, string destination, IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => WithAppDocuments(udid, bundleId, ct,
            (api, afc) => ImportAppFiles(api, afc, devicePaths, destination, progress, ct)), ct);

    /// <summary>Copy one app document to a new local preview file using its app's AFC service.</summary>
    public static Task CopyAppFileToFileAsync(string udid, string bundleId, AppFileItem item,
        string destination, CancellationToken ct = default)
        => Task.Run(() => WithAppDocuments(udid, bundleId, ct, (api, afc) =>
        {
            CopyAppPreviewFile(api, afc, item, destination, ct);
            return true;
        }), ct);

    private static void CopyAppPreviewFile(IAfcApi api, AfcClientHandle afc, AppFileItem item,
        string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureAppParents(api, afc, item.DevicePath);
        var current = ReadAppEntry(api, afc, item.DevicePath);
        if (current.IsDirectory || current.IsSymbolicLink) throw new IOException("일반 파일만 미리볼 수 있습니다.");
        if (current.Size != item.Size) throw new IOException("원본 크기가 달라졌습니다. 앱 파일 목록을 다시 불러오세요.");
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            ReadDeviceFile(api, afc, item.DevicePath, partial, ct);
            ct.ThrowIfCancellationRequested();
            if (new FileInfo(partial).Length != item.Size) throw new IOException("앱 미리보기 파일 크기가 원본과 다릅니다.");
            File.Move(partial, destination); // A preview must never replace an existing local file.
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private static T WithAppDocuments<T>(string udid, string bundleId, CancellationToken ct,
        Func<IAfcApi, AfcClientHandle, T> action)
    {
        ct.ThrowIfCancellationRequested();
        using var device = OpenDevice(udid);
        var ha = Lib.HouseArrest;
        Check(ha.house_arrest_client_start_service(device, out var client, Label), "앱 파일 공유 서비스 접근 실패");
        using (client)
        {
            Check(ha.house_arrest_send_command(client, "VendDocuments", bundleId), "앱 문서 요청 실패");
            Check(ha.house_arrest_get_result(client, out var response), "앱 문서 응답 없음");
            using (response)
            {
                uint length = 0;
                Lib.Plist.plist_to_xml(response, out string xml, ref length);
                ValidateAppAccessResult(xml);
            }
            Check(ha.afc_client_new_from_house_arrest_client(client, out var afc), "앱 파일 영역 접근 실패");
            // AFC must be disposed before the House Arrest connection it borrows.
            using (afc) { ct.ThrowIfCancellationRequested(); return action(Lib.Afc, afc); }
        }
    }

    private static void ValidateAppAccessResult(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        var dict = XDocument.Load(reader).Root?.Element("dict") ?? throw new MobileDeviceException("앱 문서 응답이 올바르지 않습니다.");
        var values = ReadPlistDict(dict);
        if (values.TryGetValue("Error", out var error))
            throw new MobileDeviceException($"앱에서 문서 접근을 허용하지 않았습니다 ({error}). 아이폰 잠금과 파일 공유 지원 여부를 확인하세요.");
        if (!values.TryGetValue("Status", out var status) || status != "Complete")
            throw new MobileDeviceException("앱 문서 접근이 완료되지 않았습니다. 다시 연결해 주세요.");
    }

    private static string ValidateAppPath(string path)
    {
        if (path != "/Documents" && !path.StartsWith("/Documents/", StringComparison.Ordinal))
            throw new ArgumentException("앱 Documents 폴더 안의 경로만 가져올 수 있습니다.");
        if (path.Contains('\\') || path.Contains('\0') || path.Split('/').Skip(1).Any(p => p is "" or "." or ".."))
            throw new ArgumentException("올바르지 않은 앱 파일 경로입니다.");
        return path;
    }

    private static AppFileItem ReadAppEntry(IAfcApi api, AfcClientHandle afc, string path)
    {
        ValidateAppPath(path);
        Check(api.afc_get_file_info(afc, path, out ReadOnlyCollection<string> info), "앱 파일 정보 조회 실패");
        if (info == null) throw new MobileDeviceException("앱 파일 정보 응답이 없습니다.");
        var values = new Dictionary<string, string>();
        for (int i = 0; i + 1 < info.Count; i += 2) values[info[i]] = info[i + 1];
        values.TryGetValue("st_ifmt", out var type);
        if (type is not ("S_IFDIR" or "S_IFREG" or "S_IFLNK")) throw new MobileDeviceException("지원하지 않는 앱 파일 형식입니다.");
        long size = 0;
        if (type == "S_IFREG" && (!values.TryGetValue("st_size", out var sizeText) ||
            !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out size)))
            throw new MobileDeviceException("앱 파일의 크기를 확인하지 못했습니다.");
        var modified = values.TryGetValue("st_mtime", out var time) ? FromAfcTime(time, default) : default;
        return new(path, path[(path.LastIndexOf('/') + 1)..], type == "S_IFDIR", size, modified, type == "S_IFLNK");
    }

    private static void EnsureAppParents(IAfcApi api, AfcClientHandle afc, string path)
    {
        ValidateAppPath(path);
        var current = "/Documents";
        foreach (var part in path.Split('/').Skip(2).Prepend(""))
        {
            if (part.Length > 0) current += "/" + part;
            if (current == path) break;
            var parent = ReadAppEntry(api, afc, current);
            if (!parent.IsDirectory || parent.IsSymbolicLink) throw new IOException("링크를 통해 앱 폴더 밖으로 이동할 수 없습니다.");
        }
    }

    private static List<AppFileItem> ReadAppDirectory(IAfcApi api, AfcClientHandle afc, string directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureAppParents(api, afc, directory);
        if (!ReadAppEntry(api, afc, directory).IsDirectory) throw new IOException("앱 폴더를 열 수 없습니다.");
        Check(api.afc_read_directory(afc, directory, out ReadOnlyCollection<string> names), "앱 폴더 목록 조회 실패");
        if (names == null) throw new MobileDeviceException("앱 폴더 목록 응답이 없습니다.");
        var items = new List<AppFileItem>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (name is "." or "..") continue;
            if (string.IsNullOrEmpty(name) || name.Contains('/') || name.Contains('\\') || name.Contains('\0'))
                throw new IOException("올바르지 않은 앱 파일 이름입니다.");
            items.Add(ReadAppEntry(api, afc, directory + "/" + name));
        }
        return items.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private sealed record AppCopyEntry(AppFileItem Item, string LocalPath);

    private static AppImportResult ImportAppFiles(IAfcApi api, AfcClientHandle afc, IReadOnlyList<string> paths,
        string destination, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(destination);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<AppCopyEntry>();
        var selected = paths.Select(ValidateAppPath).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var path in selected.Where(p => !selected.Any(parent => p != parent && p.StartsWith(parent + "/", StringComparison.Ordinal))))
        {
            EnsureAppParents(api, afc, path);
            Plan(ReadAppEntry(api, afc, path), root, 0);
        }
        var fileCount = plan.Count(p => !p.Item.IsDirectory);
        var totalBytes = plan.Sum(p => p.Item.Size);
        long doneBytes = 0;
        int index = 0;
        Directory.CreateDirectory(root);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var entry in plan)
        {
            ct.ThrowIfCancellationRequested();
            var item = entry.Item;
            if (item.IsDirectory)
            {
                // Each folder is new: never merge an imported tree into an existing folder or link.
                if (File.Exists(entry.LocalPath) || Directory.Exists(entry.LocalPath)) throw new IOException("저장할 폴더가 이미 생겼습니다. 다른 저장 위치를 선택해 주세요.");
                Directory.CreateDirectory(entry.LocalPath);
                continue;
            }
            index++;
            var partial = Path.Combine(Path.GetDirectoryName(entry.LocalPath)!, $".iPhoneTransfer-{Guid.NewGuid():N}.partial");
            try
            {
                progress?.Report(new(index, fileCount, item.Name, doneBytes, totalBytes));
                ReadDeviceFile(api, afc, item.DevicePath, partial, ct, bytes =>
                {
                    doneBytes += bytes;
                    if (sw.ElapsedMilliseconds >= 100)
                    {
                        progress?.Report(new(index, fileCount, item.Name, doneBytes, totalBytes)); sw.Restart();
                    }
                });
                ct.ThrowIfCancellationRequested();
                if (new FileInfo(partial).Length != item.Size) throw new IOException("앱 원본 크기와 전송한 크기가 다릅니다. 파일 목록을 새로 불러와 주세요.");
                File.Move(partial, entry.LocalPath); // Never overwrites an existing file.
                if (item.Modified != default) { try { File.SetLastWriteTime(entry.LocalPath, item.Modified); } catch { } }
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
        progress?.Report(new(fileCount, fileCount, "", totalBytes, totalBytes));
        return new(fileCount, plan.Count(p => p.Item.IsDirectory), totalBytes);

        void Plan(AppFileItem item, string parent, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 64) throw new IOException("폴더 깊이가 너무 큽니다. 하위 폴더를 열어 나누어 가져와 주세요.");
            if (item.IsSymbolicLink) throw new IOException($"링크는 가져올 수 없습니다: {item.Name}. 일반 파일이나 폴더를 선택하세요.");
            var local = ReserveLocalPath(parent, item.Name, item.IsDirectory, reserved);
            plan.Add(new(item, local));
            if (item.IsDirectory)
                foreach (var child in ReadAppDirectory(api, afc, item.DevicePath, ct)) Plan(child, local, depth + 1);
        }
    }

    private static string ReserveLocalPath(string directory, string name, bool isDirectory, HashSet<string> reserved)
    {
        const string invalid = "<>:\"/\\|?*";
        name = new string(name.Select(c => c < 32 || invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        if (name.Length == 0) name = "_";
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[1-9¹²³]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) name = "_" + name;
        var extension = isDirectory ? "" : Path.GetExtension(name);
        var baseName = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        for (int i = 0; ; i++)
        {
            var candidate = Path.Combine(directory, i == 0 ? name : $"{baseName} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate) && reserved.Add(candidate)) return candidate;
        }
    }
}
