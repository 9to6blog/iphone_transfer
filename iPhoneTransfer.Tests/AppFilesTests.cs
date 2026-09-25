using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.ExceptionServices;
using iMobileDevice.Afc;
using iPhoneTransfer.Core;

internal static class AppFilesTests
{
    internal static void Run(string directory, Action<string> pass)
    {
        object? Call(string method, params object?[] args)
        {
            try { return typeof(IPhoneClient).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args); }
            catch (TargetInvocationException ex) { ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); throw; }
        }
        void Check(bool value, string message) { if (!value) throw new Exception(message); pass(message); }
        void Fails<T>(Action action, string message) where T : Exception
        {
            try { action(); throw new Exception("Expected " + typeof(T).Name); }
            catch (T) { pass(message); }
        }
        var data = new byte[] { 12, 23, 34, 45, 56, 67, 78 };
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal)
        {
            ["/Documents"] = new(true),
            ["/Documents/report.pdf"] = new(false, data),
            ["/Documents/자료"] = new(true),
            ["/Documents/자료/book.epub"] = new(false, data),
            ["/Documents/자료/empty"] = new(true),
            ["/Documents/자료/sub"] = new(true),
            ["/Documents/자료/sub/archive.zip"] = new(false, data),
            ["/Documents/자료/sub/A.txt"] = new(false, data),
            ["/Documents/자료/sub/a.txt"] = new(false, data),
            ["/Documents/자료/sub/NUL.txt"] = new(false, data),
            ["/Documents/자료/sub/name:one?.bin"] = new(false, []),
            ["/Documents/link"] = new(false, Link: true)
        };
        var api = DispatchProxy.Create<IAfcApi, FakeAfc>();
        var handles = new Dictionary<ulong, (string Path, int Offset)>();
        ulong nextHandle = 0;
        int writes = 0, reads = 0;
        bool failRead = false;
        Action? afterRead = null;
        ((FakeAfc)(object)api).Handler = (method, args) =>
        {
            switch (method.Name)
            {
                case "afc_get_file_info":
                    if (!entries.TryGetValue((string)args[1]!, out var info)) return AfcError.ObjectNotFound;
                    args[2] = new ReadOnlyCollection<string>(new[] { "st_ifmt", info.Link ? "S_IFLNK" : info.Directory ? "S_IFDIR" : "S_IFREG",
                        "st_size", (info.Size ?? info.Data?.Length ?? 0).ToString(), "st_mtime", "1700000000000000000" });
                    return AfcError.Success;
                case "afc_read_directory":
                    var path = (string)args[1]!;
                    args[2] = new ReadOnlyCollection<string>(entries.Keys.Where(p => p.StartsWith(path + "/", StringComparison.Ordinal) && !p[(path.Length + 1)..].Contains('/'))
                        .Select(p => p[(path.Length + 1)..]).Prepend(".").Prepend("..").ToArray());
                    return AfcError.Success;
                case "afc_file_open":
                    if ((AfcFileMode)args[2]! != AfcFileMode.FopenRdonly) { writes++; throw new Exception("Import attempted a device write"); }
                    var handle = ++nextHandle; handles[handle] = ((string)args[1]!, 0); args[3] = handle;
                    return AfcError.Success;
                case "afc_file_read":
                    if (failRead) return AfcError.ReadError;
                    var key = (ulong)args[1]!;
                    var state = handles[key];
                    var bytes = entries[state.Path].Data!;
                    var n = Math.Min(2, bytes.Length - state.Offset);
                    bytes.AsSpan(state.Offset, n).CopyTo((byte[])args[2]!); args[4] = (uint)n;
                    handles[key] = (state.Path, state.Offset + n); reads++; afterRead?.Invoke();
                    return AfcError.Success;
                case "afc_file_close": handles.Remove((ulong)args[1]!); return AfcError.Success;
                default: writes++; throw new Exception("Unexpected device mutation: " + method.Name);
            }
        };
        var listed = (List<AppFileItem>)Call("ReadAppDirectory", api, null, "/Documents", CancellationToken.None)!;
        Check(listed.Count == 3 && listed[0].IsDirectory && listed.Any(i => i.Name == "report.pdf"), "App browser lists folders and non-photo files");
        Check(listed.Single(i => i.Name == "link").IsSymbolicLink, "App browser marks symbolic links explicitly");
        var nested = (List<AppFileItem>)Call("ReadAppDirectory", api, null, "/Documents/자료/sub", CancellationToken.None)!;
        Check(nested.Count == 5, "App browser opens nested Unicode directories");
        var output = Path.Combine(directory, "app-import"); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "report.pdf"), "keep");
        Directory.CreateDirectory(Path.Combine(output, "report (1).pdf"));
        Directory.CreateDirectory(Path.Combine(output, "자료"));
        File.WriteAllText(Path.Combine(output, "자료", "keep.txt"), "keep");
        var imported = (AppImportResult)Call("ImportAppFiles", api, null,
            new[] { "/Documents/report.pdf", "/Documents/자료", "/Documents/자료/book.epub", "/Documents/report.pdf" }, output, null, CancellationToken.None)!;
        Check(imported.Files == 7 && imported.Folders == 3, "Folder imports include nested and empty folders, without duplicate selections");
        Check(File.ReadAllBytes(Path.Combine(output, "report (2).pdf")).SequenceEqual(data) && File.ReadAllText(Path.Combine(output, "report.pdf")) == "keep", "Existing files and folders are preserved by numbered names");
        Check(Directory.Exists(Path.Combine(output, "자료 (1)", "empty")) && File.Exists(Path.Combine(output, "자료", "keep.txt")), "Import preserves folder structure and existing folders");
        var children = Directory.GetFiles(Path.Combine(output, "자료 (1)", "sub"));
        Check(children.Length == 5 && children.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 5 &&
            children.Any(p => Path.GetFileName(p) == "_NUL.txt") && children.Any(p => Path.GetFileName(p) == "name_one_.bin"),
            "Windows reserved names, invalid characters and case collisions are safely renamed");
        Check(reads > imported.Files && writes == 0 && handles.Count == 0, "App imports stream chunks, close handles and never write to the iPhone");
        var previewPath = Path.Combine(directory, "app-preview.bin");
        var previewItem = listed.Single(i => i.Name == "report.pdf");
        Call("CopyAppPreviewFile", api, null, previewItem, previewPath, CancellationToken.None);
        Check(File.ReadAllBytes(previewPath).SequenceEqual(data) && writes == 0 && handles.Count == 0, "App previews stream exact bytes from the app document service, read-only");
        Fails<IOException>(() => Call("CopyAppPreviewFile", api, null, previewItem, previewPath, CancellationToken.None), "Preview copies never overwrite an existing local file");
        Fails<IOException>(() => Call("CopyAppPreviewFile", api, null, listed.Single(i => i.IsSymbolicLink), previewPath, CancellationToken.None), "App preview rejects symbolic links");
        entries["/Documents/report.pdf"] = new(false, data, Size: 100);
        Fails<IOException>(() => Call("CopyAppPreviewFile", api, null, previewItem, previewPath, CancellationToken.None), "App preview rejects changed file size before reading");
        entries["/Documents/report.pdf"] = new(false, data);
        var failedPreview = Path.Combine(directory, "failed-preview.bin");
        failRead = true;
        Fails<MobileDeviceException>(() => Call("CopyAppPreviewFile", api, null, previewItem, failedPreview, CancellationToken.None), "App preview propagates USB read failures");
        failRead = false;
        using (var previewCancel = new CancellationTokenSource())
        {
            afterRead = previewCancel.Cancel;
            Fails<OperationCanceledException>(() => Call("CopyAppPreviewFile", api, null, previewItem, failedPreview, previewCancel.Token), "App preview cancels in the middle of a file");
            afterRead = null;
        }
        Check(!File.Exists(failedPreview) && !Directory.GetFiles(directory, "*.partial").Any() && handles.Count == 0,
            "Failed or canceled app previews remove temporary files and close native handles");
        var failureDir = Path.Combine(directory, "app-failures");
        entries["/Documents/report.pdf"] = new(false, data, Size: 100);
        Fails<IOException>(() => Call("ImportAppFiles", api, null, new[] { "/Documents/report.pdf" }, failureDir, null, CancellationToken.None), "Size mismatch cannot publish a completed app file");
        Check(!Directory.EnumerateFileSystemEntries(failureDir).Any(), "Failed app import removes partial file");
        entries["/Documents/report.pdf"] = new(false, data);
        failRead = true;
        Fails<MobileDeviceException>(() => Call("ImportAppFiles", api, null, new[] { "/Documents/report.pdf" }, failureDir, null, CancellationToken.None), "USB read failure interrupts app import");
        failRead = false;
        using var cancel = new CancellationTokenSource(); afterRead = cancel.Cancel;
        Fails<OperationCanceledException>(() => Call("ImportAppFiles", api, null, new[] { "/Documents/report.pdf" }, failureDir, null, cancel.Token), "Cancel mid-file stops app import");
        afterRead = null;
        Check(!Directory.EnumerateFileSystemEntries(failureDir).Any() && handles.Count == 0, "Canceled and failed imports leave no partial files or open handles");
        Fails<ArgumentException>(() => Call("ReadAppDirectory", api, null, "/Documents/../Library", CancellationToken.None), "Traversal outside Documents is rejected");
        Fails<IOException>(() => Call("ImportAppFiles", api, null, new[] { "/Documents/link" }, failureDir, null, CancellationToken.None), "Symbolic links are never imported or followed");
        Fails<IOException>(() => Call("ReadAppDirectory", api, null, "/Documents/link/child", CancellationToken.None), "Symbolic-link parents cannot escape the shared folder");
        Fails<MobileDeviceException>(() => Call("ValidateAppAccessResult", "<plist><dict><key>Error</key><string>ApplicationLookupFailed</string></dict></plist>"), "App sharing rejection is shown before opening AFC");
        Call("ValidateAppAccessResult", "<plist><dict><key>Status</key><string>Complete</string></dict></plist>");
        pass("App sharing success response is accepted");
    }

    private sealed record Entry(bool Directory, byte[]? Data = null, long? Size = null, bool Link = false);
}
