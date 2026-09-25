using System.Collections.ObjectModel;
using System.Reflection;
using iMobileDevice.Afc;
using iPhoneTransfer.Core;

var results = new List<string>();
var directory = Path.Combine(Path.GetTempPath(), "iPhoneTransfer-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
void Pass(string name) { results.Add(name); Console.WriteLine("PASS " + name); }
object? Invoke(string method, params object?[] args)
{
    try { return typeof(IPhoneClient).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args); }
    catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); throw; }
}
void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); throw new Exception("Expected " + typeof(T).Name + ": " + name); }
    catch (T) { Pass(name); }
}
IAfcApi Fake(Func<MethodInfo, object?[], object?> callback)
{
    var api = DispatchProxy.Create<IAfcApi, FakeAfc>();
    ((FakeAfc)(object)api).Handler = callback;
    return api;
}
int closes = 0;
var errorApi = Fake((m, a) =>
{
    if (m.Name == "afc_file_open") { a[3] = 1UL; return AfcError.Success; }
    if (m.Name == "afc_file_close") { closes++; return AfcError.Success; }
    return (AfcError)1;
});
Throws<MobileDeviceException>(() => Invoke("ReadDeviceFile", errorApi, null, "/DCIM/a", Path.Combine(directory, "read-error"), CancellationToken.None, null), "USB read error is not EOF/success");
if (closes != 1) throw new Exception("Handle leaked on read error"); Pass("Failed read closes native file handle");
Throws<MobileDeviceException>(() => Invoke("ReadOpenFile", errorApi, null, "/DCIM/a", CancellationToken.None), "Preview read errors propagate");
Throws<MobileDeviceException>(() => Invoke("WalkDir", errorApi, null, "/DCIM", new List<PhotoItem>(), CancellationToken.None), "DCIM access failure is not an empty library");
Throws<MobileDeviceException>(() => Invoke("GetInfo", errorApi, null, "/DCIM/a"), "File metadata errors are not zero byte files");
var payload = new byte[] { 10, 20, 30, 40 };
int readCalls = 0;
var readApi = Fake((m, a) =>
{
    if (m.Name == "afc_file_open") a[3] = 1UL;
    if (m.Name == "afc_file_read") { var n = readCalls++ == 0 ? payload.Length : 0; payload.AsSpan(0, n).CopyTo((byte[])a[2]!); a[4] = (uint)n; }
    return AfcError.Success;
});
var target = Path.Combine(directory, "complete");
Invoke("ReadDeviceFile", readApi, null, "/DCIM/a", target, CancellationToken.None, null);
if (!File.ReadAllBytes(target).SequenceEqual(payload)) throw new Exception("Payload mismatch"); Pass("Complete read preserves exact bytes");
using var canceled = new CancellationTokenSource(); canceled.Cancel();
Throws<OperationCanceledException>(() => Invoke("ReadDeviceFile", readApi, null, "/DCIM/a", Path.Combine(directory, "cancel"), canceled.Token, null), "Cancellation stops file reads");
var zeroApi = Fake((m, a) => { if(m.Name == "afc_file_open") a[3]=1UL; if(m.Name == "afc_file_write") a[4]=0U; return AfcError.Success; });
Throws<MobileDeviceException>(() => Invoke("WriteDeviceFile", zeroApi, null, "/Documents/a", target, CancellationToken.None, null), "Zero byte writes fail instead of looping forever");
var uploaded = new List<byte>();
var partialApi = Fake((m, a) =>
{
    if(m.Name == "afc_file_open") a[3]=1UL;
    if(m.Name == "afc_file_write") { uint n=Math.Min(2U,(uint)a[3]!); uploaded.AddRange(((byte[])a[2]!).Take((int)n)); a[4]=n; }
    return AfcError.Success;
});
Invoke("WriteDeviceFile", partialApi, null, "/Documents/a", target, CancellationToken.None, null);
if(!uploaded.SequenceEqual(payload)) throw new Exception("Partial write corruption"); Pass("Short USB writes retry remaining bytes without duplication");
int collisions=0;
var collisionApi=Fake((m,a)=> ++collisions <= 2 ? AfcError.Success : AfcError.ObjectNotFound);
var renamed=(string)Invoke("UniqueDevicePath",collisionApi,null,"/Documents/a.txt")!;
if(renamed!="/Documents/a (2).txt")throw new Exception("Remote collision handling failed");Pass("Existing remote files receive a new name");
Throws<MobileDeviceException>(()=>Invoke("UniqueDevicePath",errorApi,null,"/Documents/a.txt"),"Remote access error cannot be mistaken for an unused filename");
var xml="<plist><array><dict><key>CFBundleIdentifier</key><string>test.shared</string><key>CFBundleDisplayName</key><string>Shared</string><key>UIFileSharingEnabled</key><true/></dict><dict><key>CFBundleIdentifier</key><string>private</string><key>UIFileSharingEnabled</key><false/></dict></array></plist>";
var apps=(List<SharingApp>)Invoke("ParseSharingApps",xml,CancellationToken.None)!;
if(apps.Count!=1 || apps[0].BundleId!="test.shared")throw new Exception("Sharing filter failed");Pass("Only file sharing enabled apps are exposed");
var listingApi = Fake((m, a) =>
{
    if (m.Name == "afc_read_directory") a[2] = new ReadOnlyCollection<string>((string)a[1]! == "/DCIM"
        ? new[] { ".", "..", "100APPLE", "101APPLE" }
        : new[] { "IMG_0001.HEIC", "IMG_0001.MOV", "IMG_0002.PNG", "IMG_0003.DNG", "IMG_0001.AAE" });
    if (m.Name == "afc_get_file_info") a[2] = new ReadOnlyCollection<string>(new[] { "st_ifmt", Path.HasExtension((string)a[1]!) ? "S_IFREG" : "S_IFDIR", "st_size", "100" });
    return AfcError.Success;
});
var listed = new List<PhotoItem>();
Invoke("WalkDir", listingApi, null, "/DCIM", listed, CancellationToken.None);
if (listed.Count != 8 || listed.Count(p => p.FileName.EndsWith(".HEIC")) != 2 || listed.Count(p => p.FileName.EndsWith(".MOV")) != 2)
    throw new Exception("Camera originals missing from nested DCIM listing");
Pass("All DCIM folders include HEIC, MOV, PNG and DNG originals without sidecars");
Console.WriteLine($"TOTAL {results.Count} PASS");

public class FakeAfc : DispatchProxy
{
    public Func<MethodInfo, object?[], object?> Handler = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args!);
}
