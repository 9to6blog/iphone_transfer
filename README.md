# iPhoneTransfer 2.0

Windows 10 / 11 x64용 아이폰 사진·파일 USB 전송 프로그램입니다.
.NET 8 런타임을 포함하여 .NET 별도 설치 없이 실행합니다.
권장: Windows 10 22H2(빌드 19045) 이상 또는 Windows 11, 최신 Apple Devices.
32비트 Windows 및 ARM64 전용 배포는 이 패키지에 포함하지 않습니다.

## 시작하기

1. `iPhoneTransfer-Setup.exe`로 설치하거나 무설치 ZIP을 **전부** 풀고 `실행.bat`를 실행합니다.
2. **연결 센터**에서 Windows, Apple 지원, USB, 신뢰 상태를 확인합니다.
3. Apple 기기 지원이 없으면 **Apple Devices 설치**를 누릅니다.
4. 데이터 전송 가능한 USB 케이블로 연결하고 아이폰을 잠금 해제합니다.
5. 아이폰의 **이 컴퓨터를 신뢰**를 승인하면 자동으로 다시 검색합니다.

## 사진 가져오기

사진 목록 불러오기 → 원하는 사진 선택 → 선택한 사진 가져오기 → PC 저장 폴더 선택.
기본은 원본 그대로 저장하며, 아이폰 원본은 삭제하지 않습니다.
`JPG로 변환`을 선택한 경우 변환 가능한 이미지만 JPG로 저장합니다. 실패하면 원본을 보관합니다.
HEIC 미리보기에는 Windows 코덱이 필요할 수 있습니다. iCloud에만 있는 원본은 먼저 기기에 다운로드해야 합니다.
USB 읽기 오류, 파일 크기 불일치, 취소는 완료로 표시하지 않습니다. 완성된 파일만 최종 이름으로 저장합니다.
동일 이름의 기존 파일은 덮어쓰지 않고 새 이름을 사용합니다.

## 앱으로 보내기

파일 공유 앱 목록 불러오기 → 앱 선택 → 파일 추가/끌어놓기 → 선택한 앱으로 보내기.
아이폰에 설치된 파일 공유 지원 앱의 Documents 폴더로 전송합니다. 사진 앱 카메라롤로 직접 넣는 기능은 아닙니다.
전송은 임시 이름에 기록한 뒤 크기를 확인해 확정하며, 기존 파일과 같은 이름이면 번호를 붙입니다.
연결이 끊겨 아이폰에 접근할 수 없으면 전송 중이던 `.iPhoneTransfer-*.partial`이 남을 수 있습니다.

## 연결 센터와 설치

- 8초 간격 자동 검색. 검사 프로세스는 22초 제한으로 느린/응답 없는 드라이버를 분리합니다.
- Apple Devices 설치: 정확한 Microsoft Store 제품 ID `9NP83LWLPZ9K`를 이용합니다.
  winget을 사용할 수 없거나 설치에 실패하면 공식 Store 페이지를 엽니다. 페이지를 연 것만으로 설치 완료로 표시하지 않습니다.
- 드라이버·서비스 복구: UAC 승인 후 기존 Apple Mobile Device Service 시작/재시작,
  설치된 `usbaapl64.inf` 재등록, 지원 OS에서 USB 재검색을 수행합니다. 다른 USB 드라이버를 삭제하지 않습니다.
- 대체 iTunes 설치: Apple Devices가 없는 환경에서만 실행합니다. winget `Apple.iTunes`를 우선 사용하고,
  없으면 Apple 공식 서버에서 다운로드하여 유효한 Apple 디지털 서명을 확인한 뒤 설치합니다.
- 설치 종료 코드, 취소, 재시작 필요 상태를 구분하고 실제 연결을 다시 검사합니다. 자동 재부팅하지 않습니다.
- 드라이버 설치에는 인터넷과 관리자 승인이 필요할 수 있습니다. Store 정책이나 방화벽, 케이블/포트 고장은 앱이 우회하지 못합니다.

Apple Devices가 설치되어 있으면 전통적인 Apple Mobile Device Service가 없어도 정상일 수 있습니다.
장치 관리자, Windows 업데이트, Apple 공식 도움말, 식별번호를 제외한 진단 보고서 저장을 화면에서 사용할 수 있습니다.
로그: `%LOCALAPPDATA%\iPhoneTransfer\Logs`.
iTunes 다운로드 캐시: `%LOCALAPPDATA%\iPhoneTransfer\InstallerCache`.

## 검증 범위

이번 변경은 Windows 11 실제 PC에서 아이폰 탐색·신뢰·AFC 접근, 사진 234개/공유 앱 11개 조회,
원본 사진 1장 가져오기와 SHA-256 일치를 확인했습니다. 앱 목록 핸들 정리 후 강제 GC 회귀 검사도 수행합니다.
가짜 USB 오류·취소·부분 쓰기·이름 충돌 테스트와 UI 상태 테스트를 포함합니다.
Windows 10 실제 PC/가상머신, ARM64, 모든 iOS 버전, 드라이버 없는 새 PC 설치, 실제 아이폰 업로드는 별도 검증 대상입니다.
어떤 케이블/기기/보안 정책에서도 연결을 100% 보장한다는 의미는 아닙니다.

## 개발

`dotnet build iPhoneTransfer.sln -c Release`
`dotnet run --project iPhoneTransfer.Tests -c Release`
`powershell -ExecutionPolicy Bypass -File build-dist.ps1`

배포 스크립트는 기존 dist를 work 아래에 보관하고 새 패키지를 만듭니다.
libimobiledevice 및 의존 DLL은 iMobileDevice-net 1.3.17에서 제공됩니다.
`THIRD-PARTY-NOTICES.md`와 `licenses`에서 원저작자·라이선스·소스 위치를 확인하세요.
Apple 드라이버 설치 파일은 배포물에 포함하지 않으며 공식 배포 경로에서 받습니다.

공식 연결 도움말: https://support.apple.com/ko-kr/108643
Apple 서비스 복구 안내: https://support.apple.com/102347
