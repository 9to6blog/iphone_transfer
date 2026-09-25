[CmdletBinding()]
param([string]$Compiler = "$PSScriptRoot\work\inno\ISCC.exe")
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($PSScriptRoot)
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$stage=Join-Path $root "work\build-$stamp"
$appOut=Join-Path $stage 'iPhoneTransfer'
New-Item -ItemType Directory -Force -Path $appOut | Out-Null
& dotnet publish (Join-Path $root 'iPhoneTransfer.App\iPhoneTransfer.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o $appOut --nologo
if($LASTEXITCODE -ne 0){throw 'Publish failed'}
foreach($name in 'imobiledevice.dll','usbmuxd.dll','plist.dll','coreclr.dll','hostfxr.dll','iPhoneTransfer.exe','install-prerequisites.ps1'){
 if(-not(Test-Path -LiteralPath (Join-Path $appOut $name))){throw "Missing runtime file: $name"}
}
Copy-Item -LiteralPath (Join-Path $root 'install-prerequisites.ps1') -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $stage '사용설명서.md')
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $appOut
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $appOut -Recurse
"@echo off`r`nstart `"`" `"%~dp0iPhoneTransfer\iPhoneTransfer.exe`"`r`n" | Set-Content -LiteralPath (Join-Path $stage '실행.bat') -Encoding ASCII
$manifest=Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
 [pscustomobject]@{Path=$_.FullName.Substring($stage.Length+1);Bytes=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
}
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding UTF8
$dist=Join-Path $root 'dist'
$backup=Join-Path $root "work\previous-dist-$stamp"
# Only move fully resolved locations within this exact project. Never recursively delete.
foreach($target in @($stage,$dist,$backup)){
 $full=[IO.Path]::GetFullPath($target)
 if(-not $full.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw "Path outside project: $full"}
}
if(Test-Path -LiteralPath $dist){Move-Item -LiteralPath $dist -Destination $backup}
Move-Item -LiteralPath $stage -Destination $dist
$zip=Join-Path $root 'iPhoneTransfer-dist.zip'
$setup=Join-Path $root 'iPhoneTransfer-Setup.exe'
foreach($artifact in @($zip,$setup)){
 if(Test-Path -LiteralPath $artifact){Copy-Item -LiteralPath $artifact -Destination (Join-Path $root ("work\previous-$stamp-"+[IO.Path]::GetFileName($artifact)))}
}
Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip -Force
if(-not(Test-Path -LiteralPath $Compiler)){throw 'Inno Setup compiler missing; portable ZIP was generated'}
& $Compiler /Qp (Join-Path $root 'installer.iss')
if($LASTEXITCODE -ne 0){throw 'Setup build failed'}
Get-Item -LiteralPath $zip,$setup | Select-Object FullName,Length

