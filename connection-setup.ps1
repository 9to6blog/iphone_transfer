[CmdletBinding()]
param([ValidateSet('Menu','Diagnose','Repair','InstallAppleDevices','InstallITunes')][string]$Action='Menu')
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$logDir=Join-Path $env:LOCALAPPDATA 'iPhoneTransfer\Logs'
function Log([string]$Text) { try { New-Item -ItemType Directory -Force -Path $logDir | Out-Null; Add-Content -LiteralPath (Join-Path $logDir 'setup.log') -Encoding UTF8 -Value "$(Get-Date -Format o) $Text" } catch {} }
function Store { try { Start-Process 'ms-windows-store://pdp/?productid=9NP83LWLPZ9K' } catch { Start-Process 'https://apps.microsoft.com/detail/9NP83LWLPZ9K' } }
function AppleService { Get-Service | Where-Object { $_.Name -eq 'Apple Mobile Device Service' -or $_.DisplayName -eq 'Apple Mobile Device Service' } | Select-Object -First 1 }
try {
 if($Action -eq 'Menu') {
  Write-Host '1: Apple Devices / 2: Repair / 3: iTunes / Other: Cancel'
  $Action=switch(Read-Host 'Number') {'1' {'InstallAppleDevices'} '2' {'Repair'} '3' {'InstallITunes'} default {exit 20}}
  if($Action -eq 'Repair') {
   $p=Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Action Repair"
   exit $p.ExitCode
  }
 }
 if($Action -eq 'Diagnose') {
  $issues=@()
  try {$app=@(Get-AppxPackage -Name AppleInc.AppleDevices -ErrorAction Stop).Count -gt 0} catch {$app=$false;$issues+='Apple Devices inspection unavailable'}
  try {$s=AppleService;$svc=if($s){[string]$s.Status}else{'NotInstalled'}} catch {$svc='Unknown';$issues+='Service inspection unavailable'}
  try {$usb=@(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object {$_.InstanceId -like 'USB\VID_05AC*'});$usbErrors=@($usb | Where-Object Status -ne 'OK').Count} catch {$usb=@();$usbErrors=0;$issues+='USB inspection unavailable'}
  [pscustomobject]@{Build=[Environment]::OSVersion.Version.Build;Architecture=$env:PROCESSOR_ARCHITECTURE;AppleDevices=$app;Service=$svc;UsbCount=$usb.Count;UsbErrors=$usbErrors;Error=if($issues.Count){$issues -join '; '}else{$null}} | ConvertTo-Json -Compress
  exit 0
 }
 Log "Begin $Action"
 if($Action -eq 'InstallAppleDevices') {
  $wg=Get-Command winget.exe -ErrorAction SilentlyContinue
  if(-not $wg){Store;Log 'Store opened; installation pending';exit 10}
  & $wg.Source install --id 9NP83LWLPZ9K --source msstore --exact --accept-source-agreements --accept-package-agreements --disable-interactivity 2>&1 | ForEach-Object {Log ([string]$_)}
  if($LASTEXITCODE -ne 0){Store;exit 10}
  if(@(Get-AppxPackage -Name AppleInc.AppleDevices).Count -eq 0){exit 11}
  exit 0
 }
 if($Action -eq 'InstallITunes') {
  if(@(Get-AppxPackage -Name AppleInc.AppleDevices).Count -gt 0){Log 'Apple Devices already installed';exit 12}
  $wg=Get-Command winget.exe -ErrorAction SilentlyContinue
  if($wg){
   & $wg.Source install --id Apple.iTunes --source winget --exact --accept-source-agreements --accept-package-agreements --disable-interactivity 2>&1 | ForEach-Object {Log ([string]$_)}
   $code=$LASTEXITCODE
   if($code -in 0,3010){exit $code}
   Log "winget exit $code; official download fallback"
  }
  [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
  $cache=Join-Path $env:LOCALAPPDATA 'iPhoneTransfer\InstallerCache'
  New-Item -ItemType Directory -Force -Path $cache | Out-Null
  $installer=Join-Path $cache ('iTunes64Setup-'+[guid]::NewGuid().ToString('N')+'.exe')
  Invoke-WebRequest -UseBasicParsing -Uri 'https://www.apple.com/itunes/download/win64' -OutFile $installer -TimeoutSec 300
  $sig=Get-AuthenticodeSignature -LiteralPath $installer
  if($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Apple Inc\.(?:,|$)'){throw 'Apple signature verification failed'}
  $p=Start-Process -FilePath $installer -ArgumentList '/passive /norestart' -Verb RunAs -WindowStyle Hidden -Wait -PassThru
  Log "Apple installer exit $($p.ExitCode)"
  exit $p.ExitCode
 }
 if($Action -eq 'Repair') {
  $id=[Security.Principal.WindowsIdentity]::GetCurrent()
  if(-not ([Security.Principal.WindowsPrincipal]::new($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){exit 21}
  $changed=$false;$reboot=$false;$s=AppleService
  if($s){Set-Service -Name $s.Name -StartupType Automatic;if($s.Status -eq 'Running'){Restart-Service -Name $s.Name -Force}else{Start-Service -Name $s.Name};$s.WaitForStatus('Running',[TimeSpan]::FromSeconds(20));$changed=$true}
  foreach($root in @($env:ProgramW6432,${env:ProgramFiles(x86)}) | Select-Object -Unique){
   if(-not $root){continue}
   $inf=Join-Path $root 'Common Files\Apple\Mobile Device Support\Drivers\usbaapl64.inf'
   if(Test-Path -LiteralPath $inf){& "$env:WINDIR\System32\pnputil.exe" /add-driver $inf /install 2>&1 | ForEach-Object {Log ([string]$_)};$code=$LASTEXITCODE;if($code -notin 0,3010){throw "Driver registration failed: $code"};if($code -eq 3010){$reboot=$true};$changed=$true}
  }
  if([Environment]::OSVersion.Version.Build -ge 19041){& "$env:WINDIR\System32\pnputil.exe" /scan-devices 2>&1 | ForEach-Object {Log ([string]$_)};if($LASTEXITCODE -ne 0){throw "USB scan failed: $LASTEXITCODE"}}
  if($reboot){exit 3010};if(-not $changed){exit 13};exit 0
 }
} catch {Log $_.Exception.Message;exit 1}

