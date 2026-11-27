# xKeep — 가족 기기 보호 (MVP)

자녀의 **Android 폰**과 **Windows PC**를 부모 앱 하나로 관리합니다. 부모 기기가 Android든 iPhone이든 같은 앱을 씁니다.
각 OS의 공식 관리자 API(Android Device Owner, Windows 서비스·WTS·WMI)만 사용합니다.

```
                     ┌────────────────────────────────────┐
 엄마(Android) ─┐    │  backend (Raspberry Pi)            │
                ├──▶ │  Fastify + SQLite + WebSocket      │ ◀── Cloudflare Tunnel (HTTPS, 무료)
 아빠(iPhone)  ─┘    │  + 부모 PWA 정적 서빙              │
   부모 PWA          └──────┬──────────────────┬──────────┘
                     WS/FCM │                  │ WS
                 ┌──────────▼───────┐  ┌───────▼──────────────────────┐
                 │ android-agent     │  │ windows-agent                │
                 │ Device Owner      │  │ LocalSystem 서비스 + UserHost │
                 └──────────────────┘  └──────────────────────────────┘
```

## 디렉토리

| 경로 | 내용 |
|---|---|
| `shared/protocol/` | 통신 규약 문서(`PROTOCOL.md`)와 예제 JSON. **변경 시 세 구현을 함께 수정** |
| `backend/` | 중계 서버(Node 22, TypeScript, Fastify) + 부모 PWA(`public/`) + Pi 배포 파일(`deploy/`) |
| `android-agent/` | Kotlin + Compose. `PolicyManager`, `UsageTracker`, `EnforcementService`, `LockActivity` |
| `windows-agent/` | .NET 8. `Agent.Service`(`ProcessMonitorWorker`, `SessionControlWorker`), `Agent.UserHost`(트레이·잠금 창) |

## 핵심 설계 원칙

1. **서버가 꺼져도 제한은 유지됩니다.** 사용 시간 계산과 잠금 판정은 각 기기가 마지막으로 받은 정책으로 직접 합니다.
   라즈베리 파이가 재부팅되거나 인터넷이 끊겨도 아이 기기 제한은 풀리지 않습니다.
2. **잠금은 명령이 아니라 상태입니다.** `policy.manualLock`이 true인 동안 잠기므로, 꺼져 있던 기기도 켜지면 바로 반영됩니다.
3. **우회되면 잠그고 알립니다(fail-closed).** 사용 기록 권한이 해제되면 즉시 잠그고 부모에게 알립니다.
   정상 종료 신호 없이 20분 이상 연결이 끊긴 기기도 부모에게 알립니다.
4. **감시보다 구조로 막습니다.** 학습 시간에는 허용한 것만 열리므로, 부모가 화면을 들여다보거나 딴짓을 "적발"할 필요가 없습니다.

## 학습 모드 (핵심 기능)

"학습을 핑계로 나무위키·디스코드" 문제를 **차단 목록이 아니라 허용 목록**으로 해결합니다.

| | 학습 모드 | 자유 시간 |
|---|---|---|
| 앱·프로그램 | 학습 허용 목록만 실행 (나머지 정지/종료) | 차단 목록만 막음 |
| 웹 | Chrome(Windows는 Edge도)에서 **허용 사이트만** 열림, 시크릿 모드 끔 | 제한 없음 |
| 시간 | 하루 한도에서 **빠지지 않음** | 하루 한도 적용 |

- **켜지는 방법**: ① 부모앱 시간표(요일·시간) ② 부모앱 "학습 시작" ③ 아이가 직접 "공부 시작"(30/60/90분). 중간에 끄는 건 부모만 가능합니다.
- **기본 허용 사이트**: `eunjae.co.kr`, `hakwonsarang.co.kr`, `ebsi.co.kr`, `ebs.co.kr`, `band.us` (하위 도메인 포함).
  기본 허용 앱: Android는 Chrome·밴드, Windows는 chrome.exe·msedge.exe. 부모앱 설정에서 바꿀 수 있습니다.
- **"학습에 필요해요" 요청**: 막힌 사이트·앱을 아이가 요청하면 부모 폰에 알림이 갑니다. 부모는 **오늘만 / 항상 / 거절** 중 하나를 누르고, 결과는 아이 기기에 알림으로 갑니다.
  - Android는 xKeep 앱에서 요청합니다.
  - Windows는 트레이 아이콘 메뉴에서 요청하거나, "쓸 수 없어요" 알림을 누르면 됩니다.
- **실시간 "딴짓 알림"은 보내지 않습니다.** 차단은 이미 시스템이 했으니, 저녁 9시 30분(`SUMMARY_TIME`)에 **하루 요약**만 갑니다. 예: "학습 100분 · 자유 50분 · 차단 3회".
  즉시 알림은 보호 장치가 깨졌을 가능성(권한 해제, 트레이 프로그램 반복 종료 등)과 아이의 요청뿐입니다.
- **내 규칙 공개**: 아이도 자기 기기에서 시간표, 허용 사이트, 남은 시간을 볼 수 있습니다. Android는 xKeep 앱, Windows는 트레이의 "내 규칙 보기"입니다.

Windows에서 학습 모드 판단 방식:
- 사용자 폴더(`C:\Users\…`, 다운로드, AppData)나 스토어 앱 폴더에서 실행된 프로그램은 허용 목록에 없으면 즉시 종료됩니다.
- 그 밖의 프로그램(`Program Files` 등)은 **화면에 창을 띄우면** 허용 목록과 비교해 종료합니다.
- `chrome.exe`처럼 **이름만 적은 항목은 Program Files 같은 보호 폴더의 파일에만** 일치합니다. 다운로드 폴더에서 `discord.exe`를 `chrome.exe`로 이름만 바꿔 실행하면 막힙니다.
- 사용자 폴더에 설치되는 프로그램(Zoom 등)은 부모앱의 "앱 목록에서 고르기"로 **전체 경로**를 추가하세요.

---

## 1. Backend + 부모 PWA

### 로컬 개발 (Mac)
```bash
cd backend
cp .env.example .env
npm install
npm run dev            # http://localhost:8080  (부모 PWA 도 같은 주소)
npm test               # API 통합 테스트 + shared fixture 스키마 검증
```
- 브라우저에서 `http://localhost:8080`에 접속해 가족 비밀번호를 설정하고, "자녀 기기 추가"에서 페어링 코드를 받습니다.
- Android 에뮬레이터에서는 Mac의 서버 주소가 `http://10.0.2.2:8080`입니다.
- 같은 와이파이의 Windows PC에서는 `http://<Mac IP>:8080`으로 접속합니다. Mac IP는 `ipconfig getifaddr en0`로 확인합니다.

### 웹 푸시(부모 알림) 키
```bash
npm run gen:vapid      # 출력된 publicKey/privateKey 를 .env 의 VAPID_* 에 입력
```

### FCM (선택, Android 절전 중 즉시 반영용)
1. Firebase 콘솔에서 무료 프로젝트를 만들고 Android 앱(`com.xkeep.agent`)을 추가합니다.
2. 받은 `google-services.json`을 `android-agent/app/`에 둡니다(git 제외됨).
3. 프로젝트 설정 → 서비스 계정 → 비공개 키 JSON을 받아 서버의 `.env`에서 `FCM_SERVICE_ACCOUNT=/경로/키.json`으로 지정합니다.

FCM 없이도 동작합니다. 다만 절전 중인 폰은 깨어나 재접속할 때 정책을 받습니다(최대 수 분 지연).

### 라즈베리 파이 배포 (상시 서버)
권장 사양은 Raspberry Pi 4/5, Raspberry Pi OS 64-bit입니다.
```bash
# Pi 에서
curl -fsSL https://deb.nodesource.com/setup_22.x | sudo -E bash -
sudo apt-get install -y nodejs
sudo useradd --system --home /opt/xkeep xkeep
sudo mkdir -p /opt/xkeep && sudo chown xkeep: /opt/xkeep

# 코드 배포 (Mac 에서 rsync 하거나 Pi 에서 git clone)
sudo -u xkeep git clone <이 저장소> /opt/xkeep
cd /opt/xkeep/backend
sudo -u xkeep npm ci && sudo -u xkeep npm run build
sudo -u xkeep cp .env.example .env && sudo -u xkeep nano .env
sudo -u xkeep mkdir -p data

sudo cp deploy/xkeep.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now xkeep
journalctl -u xkeep -f
```
SQLite DB는 `data/xkeep.db` 한 파일입니다. 백업은 이 파일만 복사하면 됩니다.

### 외부 접속: Cloudflare Tunnel (무료, 포트 개방 불필요)
iPhone 웹 푸시와 외부 접속에는 HTTPS가 필요합니다.

**A. 테스트용 (도메인 불필요, 주소가 매번 바뀜)**
```bash
cloudflared tunnel --url http://localhost:8080
# → https://xxxx.trycloudflare.com  (재시작하면 주소가 바뀌어 기기를 다시 페어링해야 함)
```

**B. 상시 운영 (Cloudflare에 등록한 도메인 필요, 연 1만 원 안팎)**
```bash
sudo apt-get install cloudflared      # 또는 Cloudflare 공식 패키지 저장소 사용
cloudflared tunnel login
cloudflared tunnel create xkeep
cloudflared tunnel route dns xkeep xkeep.<내도메인>
cat > ~/.cloudflared/config.yml <<EOF
tunnel: xkeep
credentials-file: /home/$USER/.cloudflared/<터널ID>.json
ingress:
  - hostname: xkeep.<내도메인>
    service: http://localhost:8080
  - service: http_status:404
EOF
sudo cloudflared service install
```
Cloudflare Tunnel은 WebSocket을 기본 지원합니다.

### 부모 앱 설치
- **엄마(Android)**: Chrome에서 `https://xkeep.<도메인>` 접속 → 메뉴 → "홈 화면에 추가"
- **아빠(iPhone)**: **Safari**에서 접속 → 공유 → "홈 화면에 추가" → 홈 화면 아이콘으로 실행 → "알림 켜기"
  - iOS는 홈 화면에 추가한 PWA에서만 웹 푸시를 받습니다(iOS 16.4 이상).
- 두 분 모두 같은 가족 비밀번호로 로그인합니다.

---

## 2. Android 에이전트

### 준비
- Android Studio(최신) 또는 JDK 17과 Android SDK가 필요합니다.
- gradle wrapper jar는 저장소에 없습니다. **Android Studio로 `android-agent/` 폴더를 열면** 자동으로 생성됩니다.
  CLI만 쓴다면 `gradle wrapper --gradle-version 8.11.1`을 1회 실행합니다(gradle 설치 필요).

### 빌드
```bash
cd android-agent
./gradlew testDebugUnitTest      # LimitEvaluator 단위 테스트
./gradlew assembleDebug          # app/build/outputs/apk/debug/app-debug.apk
```

### 에뮬레이터로 먼저 검증 (권장)
Device Owner는 **기기에 계정이 하나도 없어야** 설정됩니다. 새 에뮬레이터에서 Google 로그인 없이 진행합니다.
```bash
# 1) Google 계정을 추가하지 않은 새 AVD 실행 (API 34 권장)
adb devices

# 2) 설치 — 디버그 빌드는 testOnly 라서 -t 필요
adb install -t app/build/outputs/apk/debug/app-debug.apk

# 3) Device Owner 지정 (onEnabled 에서 삭제 차단·안전 모드 차단 등 즉시 적용)
adb shell dpm set-device-owner com.xkeep.agent/.admin.AgentAdminReceiver

# 4) 사용 기록 접근 허용 (Device Owner 여도 자동 부여되지 않는 특수 권한)
adb shell appops set com.xkeep.agent GET_USAGE_STATS allow

# 5) 앱 실행 → 서버 주소 http://10.0.2.2:8080 + 부모 PWA 에서 받은 6자리 코드 → 연결
adb shell am start -n com.xkeep.agent/.ui.MainActivity
```

### 확인
```bash
adb shell dpm list-owners                                    # Device Owner 확인
adb shell dumpsys user | grep -A20 "Restrictions"            # no_safe_boot, no_factory_reset 등
adb shell pm list packages --suspended                       # 정지된 앱 목록 (차단/잠금 시)
adb logcat -s PolicyManager AgentSocket EnforcementService
```
이어서 부모 PWA에서 다음을 해 봅니다.
- **지금 잠금**: 수 초 안에 잠금 화면이 뜨고 홈 버튼이 막힙니다.
- **잠금 해제**: 해제됩니다.
- **하루 허용 시간을 1분으로**: 1분 후 잠깁니다.
- **+30분**: 해제됩니다.
- **차단 앱에 `com.android.chrome`**: Chrome이 회색 처리되고 실행되지 않습니다.

### 개발 중 Device Owner 해제 (공장 초기화 없이)
```bash
adb shell dpm remove-active-admin com.xkeep.agent/.admin.AgentAdminReceiver   # testOnly 빌드만 가능
adb uninstall com.xkeep.agent
```

### 실제 자녀 폰에 설치
> 먼저 **별도 테스트 폰**에서 아래 4장 시나리오를 모두 확인한 뒤 진행하는 걸 권장합니다.

1. **공장 초기화**한 뒤, 설정 마법사에서 **Google 계정 로그인을 건너뜁니다**.
   초기화 없이 하려면 설정 → 계정에서 **모든 계정(Google, Samsung, 앱 계정)을 잠시 로그아웃**합니다(`adb shell dumpsys account`로 확인). 그 상태에서 3)을 수행한 뒤 다시 로그인하면 됩니다.
2. 설정 → 휴대전화 정보 → 빌드번호 7번 탭 → 개발자 옵션에서 USB 디버깅을 켭니다.
3. 위 2)~5)를 똑같이 수행합니다. 릴리스 빌드라면 `-t` 없이 설치합니다.
4. 그 다음에 Google 계정을 추가하고 Play 스토어를 씁니다.
5. 릴리스 빌드는 `DISALLOW_DEBUGGING_FEATURES`로 개발자 옵션을 막습니다. 디버그 빌드는 adb를 쓰도록 열어 둡니다.

> Family Link와는 함께 쓸 수 없습니다(둘 다 기기 관리자 역할).

---

## 3. Windows 에이전트

### 전제 조건 (가장 중요)
- **자녀 Windows 계정은 반드시 "표준 사용자"여야 합니다.** 관리자 계정이면 서비스를 멈출 수 있어 어떤 보호도 의미가 없습니다.
  ```powershell
  net localgroup Administrators      # 자녀 계정이 없어야 함
  ```
- 부모용 관리자 계정은 별도로 두고 비밀번호를 설정합니다.

### 빌드 (Windows, .NET 8 SDK)
```powershell
cd windows-agent
dotnet test tests\Agent.Tests                   # 판정 로직·프로토콜 단위 테스트 (Mac 에서도 가능)
dotnet publish src\Agent.Service  -c Release -o publish
dotnet publish src\Agent.UserHost -c Release -o publish
# → publish\XKeep.Agent.Service.exe, publish\XKeep.Agent.UserHost.exe (같은 폴더 필수)
```

### 설치 (관리자 PowerShell)
```powershell
# 스크립트로 한 번에: 복사 → 페어링 → 서비스 등록 → 실패 시 자동 재시작 → 안전 모드 등록 → 시작
Set-ExecutionPolicy -Scope Process Bypass
.\install\install.ps1 -Server https://xkeep.<도메인> -Code 123456
```

수동으로 등록할 때는 **cmd.exe를 관리자 권한으로** 열고 다음을 실행합니다.
```bat
xcopy /E /I publish "C:\Program Files\xKeep"
"C:\Program Files\xKeep\XKeep.Agent.Service.exe" pair --server https://xkeep.<도메인> --code 123456

sc.exe create xKeepAgent binPath= "\"C:\Program Files\xKeep\XKeep.Agent.Service.exe\"" start= auto obj= LocalSystem DisplayName= "xKeep Agent"
sc.exe failure xKeepAgent reset= 0 actions= restart/5000/restart/5000/restart/5000
reg add "HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\xKeepAgent" /ve /d Service /f
reg add "HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\xKeepAgent" /ve /d Service /f
sc.exe start xKeepAgent
```

### 확인
```powershell
sc.exe query xKeepAgent
Get-WinEvent -LogName Application -MaxEvents 30 | ? ProviderName -eq xKeepAgent   # 경고 이상 로그
Get-Content "$env:ProgramData\xKeep\usage.json"     # 관리자만 읽을 수 있음 (ACL 확인용)
```
자녀(표준 사용자) 계정으로 로그인해 다음을 확인합니다.
1. 작업 관리자 → 서비스 → xKeepAgent 중지를 누르면 "액세스 거부"가 떠야 합니다.
2. 트레이에 방패 아이콘이 있고, 마우스를 올리면 남은 시간이 보입니다.
3. 부모 PWA에서 차단에 `notepad.exe`를 추가하고 메모장을 실행하면 즉시 종료되고 알림이 뜹니다.
4. 부모 PWA에서 "지금 잠금"을 누르면 잠금 창과 함께 Windows 잠금 화면으로 바뀝니다.
   다시 로그인해 2분간 버티면 세션이 끊겨 로그인 화면으로 돌아갑니다.
5. 작업 관리자로 `XKeep.Agent.UserHost.exe`를 종료해도 1분 안에 다시 실행됩니다.

### 개발 중 콘솔 실행
UserHost 실행(`WTSQueryUserToken`)에는 SYSTEM 권한이 필요합니다. 그래서 디버깅도 서비스로 설치하는 방법을 권장합니다.
프로세스 감시나 서버 연결만 볼 때는 관리자 콘솔에서 `XKeep.Agent.Service.exe`를 직접 실행해도 됩니다.

### 제거 (부모 관리자 계정)
```powershell
.\install\uninstall.ps1
```

---

## 4. 통합 테스트 시나리오

| # | 시나리오 | 기대 결과 |
|---|---|---|
| 1 | 부모 PWA에서 Android·Windows 각각 페어링 | 두 기기 모두 "연결됨" |
| 2 | 엄마 폰에서 "지금 잠금" | 두 자녀 기기 수 초 내 잠금, 아빠 폰 PWA도 상태 갱신 |
| 3 | 하루 허용 시간 1분 | 1~2분 내 잠금, 사유 "시간 초과" |
| 4 | "+30분" | 즉시 해제, 남은 시간 표시 |
| 5 | 라즈베리 파이 전원 차단 | 자녀 기기 제한 그대로 유지 |
| 6 | 파이 복구 | 자동 재접속, 그 사이 사용량 반영 |
| 7 | Android에서 사용 기록 권한 해제(설정) | 즉시 잠금 + 부모 푸시 "우회 시도 의심" |
| 8 | 자녀 PC 랜선 뽑기 20분 | 부모 푸시 "연결 끊김" |
| 9 | 자녀 PC 정상 종료 | 연결 끊김 알림 **없음**(goodbye) |
| 10 | 부모앱 "학습 시작" 30분 | Android: Chrome·밴드 외 앱 회색(정지). Windows: 디스코드 실행 즉시 종료 + "쓸 수 없어요" 알림 |
| 11 | 학습 중 Chrome에서 `namu.wiki` / `ebsi.co.kr` | 나무위키는 "관리자가 차단" 화면, EBSi는 정상 (`chrome://policy`에서 URLAllowlist 확인) |
| 12 | 학습 중 아이가 `youtube.com` 요청 → 부모 "오늘만" | 부모 폰 알림 → 승인 즉시 열림, 아이 기기에 결과 알림. 다음 날엔 다시 막힘 |
| 13 | 하루 한도 1분 + 학습 시간표 안 | 학습 중엔 잠기지 않음, 시간표가 끝나면 잠김 |
| 14 | 아이가 "공부 시작 60분" | 학습 모드 시작. 아이 쪽에는 끝내기 버튼 없음, 부모앱에서만 종료 |
| 15 | 학습 중 작업 관리자로 트레이(UserHost) 3번 종료 | 매번 5~10초 안에 다시 실행, 3번째에 부모 푸시 "우회 시도 의심" |
| 16 | 다운로드 폴더에 아무 exe를 `chrome.exe`로 이름 바꿔 실행(학습 중) | 즉시 종료 |
| 17 | 21:30 이후 | 부모 폰에 "오늘 요약" 1회 |

---

## 5. 알려진 한계와 다음 단계

**Android**
- 복구 모드(전원 + 볼륨 키)에서 공장 초기화하는 것은 OS 정책으로 완전히 막을 수 없습니다. 초기화되면 연결 끊김 알림으로 감지합니다.
- 릴리스용 Device Owner 해제는 아직 UI가 없습니다(`PolicyManager.releaseOwnership()`). 부모 인증과 연결해야 합니다.
- 앱별 시간 제한(예: 유튜브만 1시간)은 다음 단계입니다. 현재는 하루 전체 시간, 차단 목록, 학습 모드를 지원합니다.
- 학습 모드의 사이트 제한은 **Chrome에만** 적용됩니다. 다른 브라우저는 학습 앱 목록에 넣지 마세요(넣지 않으면 정지됨).
  허용 앱 **안의 내장 브라우저**(예: 밴드 앱 안에서 링크 열기)는 제한되지 않습니다.
- 허용 사이트가 영상 플레이어 등을 **다른 도메인에서 끼워 넣는 경우**(iframe) 그 부분이 막힐 수 있습니다. 처음 며칠은 아이의 요청으로 채워 나가세요.

**Windows**
- 자유 시간의 **차단 목록**은 실행 파일 이름으로 비교하므로 이름을 바꾼 복사본은 못 잡습니다.
  학습 모드는 허용 목록 + 경로 확인이라 이 우회가 통하지 않습니다.
- 학습 모드의 사이트 제한은 Chrome·Edge 정책(레지스트리)입니다. 도메인에 가입하지 않은 가정용 PC에서도 `URLBlocklist`/`URLAllowlist`는 적용되지만, **첫 테스트에서 `chrome://policy`로 꼭 확인**하세요.
  Chrome을 사용자 폴더(AppData)에 설치했다면 학습 모드에서 종료됩니다. 관리자 권한으로 Program Files에 설치하세요.
- 학습 모드에서는 사용자 폴더에서 실행되는 백그라운드 프로그램(OneDrive 등)도 종료됩니다. 필요하면 전체 경로로 허용하세요.
- 자유 시간의 웹사이트 차단(유튜브 등)은 범위 밖입니다. 필요하면 DNS 필터(예: NextDNS 무료)를 권장합니다.
- 고급 시작 옵션의 명령 프롬프트(복구 환경)는 BitLocker를 켜거나 BIOS 비밀번호로 막아야 합니다.

**공통**
- 부모 인증은 가족 공용 비밀번호 하나입니다. 부모별 계정과 다중 가족 지원은 범위 밖입니다(가족 내 사용 전제).
- 부모 앱을 네이티브로 옮길 때(Flutter 등) 서버 API는 그대로 쓸 수 있습니다.
