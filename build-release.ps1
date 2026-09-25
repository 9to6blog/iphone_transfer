[CmdletBinding()]
param([string]$Compiler = "$PSScriptRoot\work\inno\ISCC.exe")
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$workRoot = Join-Path $root 'work'
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
$lock = [IO.File]::Open((Join-Path $workRoot 'release.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
function Assert-InRoot([string]$Path, [string]$RootPath) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $boundary = [IO.Path]::GetFullPath($RootPath).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe output path: $resolved" }
    return $resolved
}
function Remove-Generated([string]$Path) {
    $checked = Assert-InRoot $Path $workRoot
    if (Test-Path -LiteralPath $checked) { Remove-Item -LiteralPath $checked -Recurse -Force }
}
try {
    Push-Location $root
    try {
        $commit = git rev-parse HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Source must be committed before release' }
        if (git status --porcelain) { throw 'Commit and back up source changes before release' }
        $branch = git branch --show-current
        $remote = git ls-remote origin "refs/heads/$branch"
        if ($LASTEXITCODE -ne 0 -or -not $remote -or ($remote -split '\s+')[0] -ne $commit) { throw 'GitHub branch must contain the exact source commit before release' }
    } finally { Pop-Location }
    if (-not (Test-Path -LiteralPath $Compiler)) { throw 'Inno Setup compiler missing' }
    $stage = Assert-InRoot (Join-Path $workRoot 'release-stage') $workRoot
    Remove-Generated $stage
    $distStage = Join-Path $stage 'dist'
    $appOut = Join-Path $distStage 'iPhoneTransfer'
    New-Item -ItemType Directory -Path $appOut -Force | Out-Null
    & dotnet run --project (Join-Path $root 'iPhoneTransfer.Tests') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core file transfer tests failed' }
    & dotnet publish (Join-Path $root 'iPhoneTransfer.App\iPhoneTransfer.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o $appOut --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    foreach ($name in 'imobiledevice.dll','usbmuxd.dll','plist.dll','coreclr.dll','hostfxr.dll','iPhoneTransfer.exe','ffmpeg.exe','Magick.Native-Q8-x64.dll','install-prerequisites.ps1') {
        if (-not (Test-Path -LiteralPath (Join-Path $appOut $name))) { throw "Missing runtime file: $name" }
    }
    Copy-Item -LiteralPath (Join-Path $root 'connection-setup.ps1') -Destination (Join-Path $distStage 'install-prerequisites.ps1')
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $distStage '사용설명서.md')
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $appOut
    Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $appOut -Recurse
    "@echo off`r`nstart `"`" `"%~dp0iPhoneTransfer\iPhoneTransfer.exe`"`r`n" | Set-Content -LiteralPath (Join-Path $distStage '실행.bat') -Encoding ASCII
    $checkRoot = Join-Path $workRoot 'release-checks'
    Remove-Generated $checkRoot
    foreach ($mode in 'media','ui') {
        $resultDir = Join-Path $checkRoot $mode
        $process = Start-Process -FilePath (Join-Path $appOut 'iPhoneTransfer.exe') -ArgumentList "--$mode-check", ('"' + $resultDir + '"') -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(180000)) { $process.Kill(); throw "$mode checks timed out" }
        $process.Refresh()
        if ($process.ExitCode -ne 0) { throw "$mode checks failed; inspect $resultDir" }
    }
    $manifest = Get-ChildItem -LiteralPath $distStage -Recurse -File | ForEach-Object {
        [pscustomobject]@{Path=$_.FullName.Substring($distStage.Length+1);Bytes=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    }
    $manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $distStage 'manifest.json') -Encoding UTF8
    @{Version='2.3.0';SourceCommit=$commit;CoreChecks='passed';MediaChecks='passed';UiChecks='passed';PhysicalIPhone='Not run by this release script; see README validation scope'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $distStage 'validation.json') -Encoding UTF8
    Compress-Archive -Path (Join-Path $distStage '*') -DestinationPath (Join-Path $stage 'iPhoneTransfer-dist.zip')
    & $Compiler /Qp ("/DAppSource=" + $appOut) ("/O" + $stage) (Join-Path $root 'installer.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed; previous release is still intact' }
    # Verify staging before rotating releases. All targets are confined to this project.
    foreach ($entry in $manifest) {
        $path = Join-Path $distStage $entry.Path
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.SHA256) { throw "Manifest mismatch: $path" }
    }
    $previous1 = Assert-InRoot (Join-Path $workRoot 'release-previous-1') $workRoot
    $previous2 = Assert-InRoot (Join-Path $workRoot 'release-previous-2') $workRoot
    Remove-Generated $previous2
    if (Test-Path -LiteralPath $previous1) { Move-Item -LiteralPath $previous1 -Destination $previous2 }
    New-Item -ItemType Directory -Path $previous1 | Out-Null
    foreach ($name in 'dist','iPhoneTransfer-dist.zip','iPhoneTransfer-Setup.exe') {
        $current = Assert-InRoot (Join-Path $root $name) $root
        $archived = Assert-InRoot (Join-Path $previous1 $name) $workRoot
        $replacement = Assert-InRoot (Join-Path $stage $name) $workRoot
        if (Test-Path -LiteralPath $current) { Move-Item -LiteralPath $current -Destination $archived }
        Move-Item -LiteralPath $replacement -Destination $current
    }
    Remove-Generated $stage
    Get-FileHash -LiteralPath (Join-Path $root 'iPhoneTransfer-dist.zip'),(Join-Path $root 'iPhoneTransfer-Setup.exe') -Algorithm SHA256 |
        Select-Object Path,Hash | Format-Table -AutoSize
} finally { $lock.Dispose() }
