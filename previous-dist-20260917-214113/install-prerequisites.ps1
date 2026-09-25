<#
  사전요구 설치: 아이폰 USB 통신을 위한 Apple Mobile Device Support 드라이버.
  이 프로그램 자체는 .NET 런타임을 내장하고 있어 별도 설치가 필요 없습니다.
  단, 어느 PC든 아이폰과 USB로 통신하려면 Apple의 모바일 기기 드라이버가 한 번은 설치되어야 합니다.

  실행:  마우스 우클릭 → "PowerShell에서 실행"
        또는  powershell -ExecutionPolicy Bypass -File .\install-prerequisites.ps1
#>
Write-Host "=== Apple Mobile Device Support 설치 도우미 ===" -ForegroundColor Cyan

# 이미 설치돼 있는지 확인
$installed = (Get-Service -Name "Apple Mobile Device Service" -ErrorAction SilentlyContinue) -ne $null
$appx      = (Get-AppxPackage -Name "*Apple*" -ErrorAction SilentlyContinue) -ne $null
if ($installed -or $appx) {
    Write-Host "이미 Apple 기기 지원이 설치되어 있는 것으로 보입니다. 아이폰을 USB로 연결해 보세요." -ForegroundColor Green
    Write-Host "(그래도 인식이 안 되면 아래에서 재설치할 수 있습니다.)"
}

Write-Host ""
Write-Host "설치 방법을 선택하세요:"
Write-Host "  1) Microsoft Store 'Apple Devices' 앱 (권장, 최신)"
Write-Host "  2) 데스크톱판 iTunes (winget, 드라이버 호환성 가장 안정적)"
Write-Host "  3) 취소"
$choice = Read-Host "번호 입력"

switch ($choice) {
    "1" {
        Write-Host "Microsoft Store의 'Apple Devices'를 엽니다…"
        Start-Process "ms-windows-store://pdp/?productid=9NP83LWLPZ9K"  # Apple Devices
    }
    "2" {
        Write-Host "winget으로 iTunes 설치 중… (시간이 걸립니다)"
        winget install --id Apple.iTunes --accept-source-agreements --accept-package-agreements
        Write-Host "설치 후 PC를 재부팅하면 드라이버가 확실히 적용됩니다." -ForegroundColor Yellow
    }
    default { Write-Host "취소했습니다." }
}
