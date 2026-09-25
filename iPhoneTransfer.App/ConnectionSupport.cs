using System.Diagnostics;
using System.IO;
using System.Text.Json;
using iPhoneTransfer.Core;

namespace iPhoneTransfer.App;

public sealed record ProbeResult(List<DeviceInfo> Devices, string? Error = null);
public sealed record HostStatus(int Build, string Architecture, bool AppleDevices, string Service,
    int UsbCount, int UsbErrors, string? Error = null);

internal static class ConnectionSupport
{
    public static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iPhoneTransfer", "Logs");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(Path.Combine(LogDirectory, $"connection-{DateTime.Today:yyyyMMdd}.log"),
                $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch { /* A read-only log directory must not stop a transfer. */ }
    }

    // Native handshake calls have no reliable managed cancellation. Isolate probes in a
    // short-lived process so a hung native driver never blocks the UI or leaks probe tasks.
    public static async Task<ProbeResult> ProbeAsync(CancellationToken ct, bool usbOnly = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"iphone-probe-{Guid.NewGuid():N}.json");
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(usbOnly ? "--usb-probe" : "--probe");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start) ?? throw new IOException("연결 검사 프로세스를 시작하지 못했습니다.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(usbOnly ? 5 : 22));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                await process.WaitForExitAsync();
                ct.ThrowIfCancellationRequested();
                return new([], "연결 응답 시간이 초과되었습니다. 잠금 해제 후 다시 연결하거나 연결 센터에서 복구하세요.");
            }
            if (!File.Exists(path)) return new([], $"연결 검사 종료 ({process.ExitCode}). 앱 파일 복구가 필요할 수 있습니다.");
            return JsonSerializer.Deserialize<ProbeResult>(await File.ReadAllTextAsync(path, ct))
                ?? new([], "연결 검사 결과를 읽을 수 없습니다.");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    public static async Task<HostStatus> InspectAsync(CancellationToken ct)
    {
        try
        {
            var script = Path.Combine(AppContext.BaseDirectory, "install-prerequisites.ps1");
            var start = PowerShell(script, "Diagnose");
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using var process = Process.Start(start) ?? throw new IOException("진단을 시작하지 못했습니다.");
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                await process.WaitForExitAsync();
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("Windows 진단 응답 시간 초과");
            }
            var json = await output;
            var error = await errors;
            if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? "Windows 진단 실패" : error.Trim());
            return JsonSerializer.Deserialize<HostStatus>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new IOException("Windows 진단 결과 없음");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(Environment.OSVersion.Version.Build, "x64", false, "Unknown", 0, 0, ex.Message); }
    }

    private static ProcessStartInfo PowerShell(string script, string action)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Action", action })
            start.ArgumentList.Add(arg);
        return start;
    }

    public static async Task<int> RunActionAsync(string action)
    {
        if (action is not ("Repair" or "InstallAppleDevices" or "InstallITunes")) throw new ArgumentException("알 수 없는 조치");
        var start = PowerShell(Path.Combine(AppContext.BaseDirectory, "install-prerequisites.ps1"), action);
        if (action == "Repair")
        {
            start.UseShellExecute = true;
            start.Verb = "runas";
            start.WindowStyle = ProcessWindowStyle.Hidden;
        }
        using var process = Process.Start(start) ?? throw new IOException("설치/복구를 시작하지 못했습니다.");
        await process.WaitForExitAsync(); // Do not kill an installer half-way through a driver installation.
        Log($"Action={action}; Exit={process.ExitCode}");
        return process.ExitCode;
    }
}
