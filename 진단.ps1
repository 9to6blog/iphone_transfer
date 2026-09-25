<#
  iPhoneTransfer 연결 진단
  아이폰이 앱에 안 잡힐 때 어느 단계에서 끊기는지 순서대로 확인한다.

  실행: 이 파일 우클릭 → "PowerShell에서 실행"
        또는  powershell -ExecutionPolicy Bypass -File .\진단.ps1
#>

$ErrorActionPreference = 'SilentlyContinue'
$dll = Join-Path $PSScriptRoot 'dist\iPhoneTransfer'
if (-not (Test-Path (Join-Path $dll 'imobiledevice.dll'))) {
    $dll = Join-Path $PSScriptRoot 'iPhoneTransfer'
}

function Step($n, $t) { Write-Host "`n[$n] $t" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  OK   $m" -ForegroundColor Green }
function Bad($m)  { Write-Host "  실패 $m" -ForegroundColor Red }
function Fix($m)  { Write-Host "  조치 → $m" -ForegroundColor Yellow }

Write-Host "=== iPhoneTransfer 연결 진단 ===" -ForegroundColor White

# ---------------------------------------------------------------------------
Step 1 "Apple 기기 지원 설치 여부"
$svc  = Get-Service -Name 'Apple Mobile Device Service'
$appx = Get-AppxPackage -Name 'AppleInc.AppleDevices'
$proc = Get-Process -Name 'AppleMobileDeviceProcess','AppleMobileDeviceService'
if ($svc)  { Ok "Apple Mobile Device Service ($($svc.Status))" }
if ($appx) { Ok "Microsoft Store 'Apple Devices' $($appx.Version)" }
if (-not $svc -and -not $appx) {
    Bad "Apple 드라이버가 설치되어 있지 않음"
    Fix "install-prerequisites.ps1 실행 → 'Apple Devices' 앱 또는 iTunes 설치 후 재부팅"
    exit 1
}
if ($proc) { Ok "데몬 실행 중: $(($proc | Select-Object -Expand ProcessName -Unique) -join ', ')" }
else       { Bad "Apple 데몬이 실행 중이 아님"; Fix "PC 재부팅 또는 'Apple Devices' 앱을 한 번 실행" }

# ---------------------------------------------------------------------------
Step 2 "USB 연결 상태"
$pnp = Get-PnpDevice | Where-Object { $_.InstanceId -like '*VID_05AC*' -and $_.Status -eq 'OK' }
if ($pnp) { $pnp | ForEach-Object { Ok $_.FriendlyName } }
else {
    Bad "USB에 아이폰이 보이지 않음"
    Fix "케이블/포트 교체(데이터 지원 케이블인지 확인), 아이폰 잠금 해제 후 다시 연결"
    exit 1
}

# ---------------------------------------------------------------------------
Step 3 "usbmuxd 대기 상태 (127.0.0.1:27015)"
$tcp = Get-NetTCPConnection -LocalPort 27015 -State Listen
if ($tcp) { Ok "리스닝 중" }
else {
    Bad "27015 포트가 열려 있지 않음 (앱이 붙을 곳이 없음)"
    Fix "'Apple Devices' 앱 실행 또는 PC 재부팅. 그래도 안 되면 apple.com 데스크톱판 iTunes 설치"
    exit 1
}

# ---------------------------------------------------------------------------
Step 4 "신뢰(페어링) 기록"
$ld = "$env:ProgramData\Apple\Lockdown"
$recs = Get-ChildItem $ld -Filter '*.plist' | Where-Object { $_.Name -ne 'SystemConfiguration.plist' }
if ($recs) { $recs | ForEach-Object { Ok "$($_.BaseName)  ($($_.LastWriteTime.ToString('yyyy-MM-dd')) 신뢰)" } }
else {
    Bad "페어링 기록 없음 — 이 PC를 아직 신뢰하지 않음"
    Fix "아이폰 잠금 해제 → USB 연결 → '이 컴퓨터를 신뢰하시겠습니까?'에서 '신뢰' 탭"
}

# ---------------------------------------------------------------------------
Step 5 "앱이 쓰는 통신 계층 (libimobiledevice)"
if (-not (Test-Path (Join-Path $dll 'imobiledevice.dll'))) {
    Bad "imobiledevice.dll 을 찾지 못함: $dll"
    exit 1
}

# 네이티브 호출이 멈추는 경우가 있어 별도 프로세스에서 돌리고 30초로 끊는다.
$work = {
    param($dll)
    $env:PATH = "$dll;$env:PATH"
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Probe {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern bool SetDllDirectory(string p);
    [DllImport("imobiledevice.dll", CallingConvention=CallingConvention.Cdecl)]
    public static extern int idevice_get_device_list(out IntPtr l, out int c);
    [DllImport("imobiledevice.dll", CallingConvention=CallingConvention.Cdecl)]
    public static extern int idevice_device_list_free(IntPtr l);
    [DllImport("imobiledevice.dll", CallingConvention=CallingConvention.Cdecl)]
    public static extern int idevice_new(out IntPtr d, [MarshalAs(UnmanagedType.LPStr)] string udid);
    [DllImport("imobiledevice.dll", CallingConvention=CallingConvention.Cdecl)]
    public static extern int idevice_free(IntPtr d);
    [DllImport("imobiledevice.dll", CallingConvention=CallingConvention.Cdecl)]
    public static extern int lockdownd_client_new_with_handshake(IntPtr d, out IntPtr c, [MarshalAs(UnmanagedType.LPStr)] string label);
    [DllImport("imobiledevice.dll", CallingConvention=CallingConvention.Cdecl)]
    public static extern int lockdownd_client_free(IntPtr c);
    [DllImport("imobiledevice.dll", CallingConvention=CallingConvention.Cdecl)]
    public static extern int lockdownd_start_service(IntPtr c, [MarshalAs(UnmanagedType.LPStr)] string id, out IntPtr s);
    [DllImport("imobiledevice.dll", CallingConvention=CallingConvention.Cdecl)]
    public static extern int afc_client_new(IntPtr d, IntPtr s, out IntPtr a);
    [DllImport("imobiledevice.dll", CallingConvention=CallingConvention.Cdecl)]
    public static extern int afc_client_free(IntPtr a);
    [DllImport("imobiledevice.dll", CallingConvention=CallingConvention.Cdecl)]
    public static extern int afc_read_directory(IntPtr a, [MarshalAs(UnmanagedType.LPStr)] string p, out IntPtr l);
}
"@
    [Probe]::SetDllDirectory($dll) | Out-Null

    $dev = [IntPtr]::Zero; $cli = [IntPtr]::Zero; $afc = [IntPtr]::Zero
    try {
        $list = [IntPtr]::Zero; $count = 0
        $rc = [Probe]::idevice_get_device_list([ref]$list, [ref]$count)
        if ($rc -ne 0 -or $count -eq 0) { return @{ stage = 'list'; rc = $rc; count = $count } }
        $udid = [Runtime.InteropServices.Marshal]::PtrToStringAnsi([Runtime.InteropServices.Marshal]::ReadIntPtr($list, 0))
        [Probe]::idevice_device_list_free($list) | Out-Null

        if ([Probe]::idevice_new([ref]$dev, $udid) -ne 0) { return @{ stage = 'open'; udid = $udid } }

        $rc = [Probe]::lockdownd_client_new_with_handshake($dev, [ref]$cli, "iPhoneTransfer-diag")
        if ($rc -ne 0) { return @{ stage = 'handshake'; rc = $rc; udid = $udid } }

        $svcp = [IntPtr]::Zero
        $rc = [Probe]::lockdownd_start_service($cli, "com.apple.afc", [ref]$svcp)
        if ($rc -ne 0) { return @{ stage = 'afcservice'; rc = $rc; udid = $udid } }

        if ([Probe]::afc_client_new($dev, $svcp, [ref]$afc) -ne 0) { return @{ stage = 'afcclient'; udid = $udid } }

        $d = [IntPtr]::Zero
        $rc = [Probe]::afc_read_directory($afc, "/DCIM", [ref]$d)
        if ($rc -ne 0) { return @{ stage = 'dcim'; rc = $rc; udid = $udid } }

        $folders = @(); $i = 0
        while ($i -le 500) {
            $p = [Runtime.InteropServices.Marshal]::ReadIntPtr($d, $i * [IntPtr]::Size)
            if ($p -eq [IntPtr]::Zero) { break }
            $n = [Runtime.InteropServices.Marshal]::PtrToStringAnsi($p)
            if ($n -ne '.' -and $n -ne '..') { $folders += $n }
            $i++
        }
        return @{ stage = 'ok'; udid = $udid; count = $count; folders = $folders }
    }
    finally {
        # 세션을 남겨두면 아이폰이 다음 연결을 거부할 수 있으므로 반드시 해제한다.
        if ($afc -ne [IntPtr]::Zero) { [Probe]::afc_client_free($afc) | Out-Null }
        if ($cli -ne [IntPtr]::Zero) { [Probe]::lockdownd_client_free($cli) | Out-Null }
        if ($dev -ne [IntPtr]::Zero) { [Probe]::idevice_free($dev) | Out-Null }
    }
}

$job = Start-Job -ScriptBlock $work -ArgumentList $dll
if (-not (Wait-Job $job -Timeout 30)) {
    Stop-Job $job; Remove-Job $job -Force
    Bad "기기 응답 없음 (30초 초과)"
    Fix "USB 케이블을 뽑았다 다시 꽂고, 아이폰 잠금을 해제한 뒤 다시 실행하세요"
    exit 1
}
$r = Receive-Job $job
Remove-Job $job -Force

switch ($r.stage) {
    'list' {
        Bad "기기 목록 조회 실패 (코드 $($r.rc), 개수 $($r.count))"
        Fix "아이폰 잠금 해제 후 재연결. 다른 아이폰 관리 프로그램이 떠 있으면 종료"
        exit 1
    }
    'open' { Bad "기기 열기 실패"; exit 1 }
    'handshake' {
        Bad "lockdownd 핸드셰이크 실패 (코드 $($r.rc))"
        switch ($r.rc) {
            -8  { Fix "이 PC를 신뢰하지 않음. 아이폰에서 '신뢰' 탭. 안 뜨면 설정 > 일반 > 전송/재설정 > 재설정 > 위치 및 개인정보 보호 재설정" }
            -18 { Fix "아이폰이 잠겨 있음. 잠금 해제 후 재시도" }
            -19 { Fix "아이폰 화면의 신뢰 대화상자에 응답 필요" }
            default { Fix "아이폰 잠금 해제 → 케이블 재연결 → PC 재부팅 순으로 시도" }
        }
        exit 1
    }
    'afcservice' { Bad "AFC 서비스 시작 실패 (코드 $($r.rc))"; Fix "아이폰 잠금 해제 후 재시도"; exit 1 }
    'afcclient'  { Bad "AFC 클라이언트 생성 실패"; exit 1 }
    'dcim'       { Bad "/DCIM 읽기 실패 (코드 $($r.rc))"; Fix "아이폰 잠금 해제 후 재시도"; exit 1 }
}

Ok "기기 $($r.count) 대 - $($r.udid)"
Ok "lockdownd 핸드셰이크 성공 (신뢰 유효)"

Step 6 "사진 폴더 접근 (AFC /DCIM)"
Ok "/DCIM 폴더 $($r.folders.Count)개 - $(($r.folders | Select-Object -First 6) -join ', ')"

Write-Host "`n=== 결과: 모든 단계 정상. 앱에서 '새로고침'을 누르면 기기가 보입니다. ===" -ForegroundColor Green
Write-Host "(앱에서 여전히 안 보이면 앱을 완전히 종료했다가 다시 실행해 보세요.)" -ForegroundColor Gray
