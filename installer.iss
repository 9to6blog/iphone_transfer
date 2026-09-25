; ============================================================
;  iPhone 사진 전송기 — 원클릭 설치 스크립트 (Inno Setup 6)
;  빌드:  ISCC.exe installer.iss   (build-dist.ps1 이 자동 호출)
;  특징:  사용자 단위 설치 → 관리자 권한/UAC 불필요(진짜 원클릭)
; ============================================================
#define AppName "iPhone 사진 전송기"
#define AppExe  "iPhoneTransfer.exe"
#define AppVer  "2.2.0"
#ifndef AppSource
  #define AppSource "dist\iPhoneTransfer"
#endif

[Setup]
AppId={{8F3A9C2E-1B4D-4E7A-9F2C-7A1E5D6B3C84}
AppName={#AppName}
AppVersion={#AppVer}
AppPublisher=iPhoneTransfer
DefaultDirName={localappdata}\Programs\iPhoneTransfer
; 사용자 단위 설치(관리자 권한 불필요)
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
DisableDirPage=yes
OutputDir={#SourcePath}
OutputBaseFilename=iPhoneTransfer-Setup
Compression=lzma2
SolidCompression=yes
MinVersion=10.0.14393
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
WizardStyle=modern
SetupIconFile=iPhoneTransfer.App\appicon.ico
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=yes

[Languages]
Name: "kr"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "autolaunch"; Description: "아이폰 USB 연결 시 자동 실행 (Windows 로그인 후 연결 대기)"; GroupDescription: "자동 실행"; Flags: checkedonce

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "iPhoneTransfer"; ValueData: """{app}\{#AppExe}"" --watch"; Tasks: autolaunch; Flags: uninsdeletevalue

[Files]
; 게시된 앱 전체(네이티브 libimobiledevice DLL 포함)
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; 문제 해결용 드라이버 설치 도우미 + 설명서
Source: "install-prerequisites.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; DestName: "사용설명서.md"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autoprograms}\Apple 드라이버 설치(기기 인식 안 될 때)"; Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\install-prerequisites.ps1"""
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--watch"; Tasks: autolaunch; Flags: nowait runhidden
Filename: "{app}\{#AppExe}"; Parameters: "--connection"; Description: "연결 센터에서 아이폰 연결 준비"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--disable-autolaunch"; Flags: runhidden waituntilterminated
