# xKeep 통신 규약 (v1)

이 문서는 서버, Android 에이전트, Windows 에이전트, 부모앱이 공유하는 규약입니다.
기준 구현은 `backend/src/protocol.ts`(zod)입니다. Kotlin/C# 모델은 필드명(camelCase)을 그대로 맞춥니다.
`fixtures/*.json` 예제는 backend 테스트(`npm test`)에서 스키마 검증됩니다.

## 설계 원칙

1. **정책은 상태다.** 잠금·해제도 일회성 명령이 아니라 `policy.manualLock`이라는 상태입니다.
   그래서 오프라인이던 기기도 재접속하면 자동으로 최신 상태가 됩니다.
2. **판정은 에이전트가 한다.** 사용 시간 계산과 잠금 판단(`LimitEvaluator`)은 각 기기가 로컬 정책 캐시로 수행합니다.
   서버가 꺼져 있어도 제한은 유지됩니다.
3. **서버가 진실의 원천이다.** 에이전트는 `policy.version`이 다르면 서버 정책으로 덮어씁니다.

## 인증

| 주체 | 방식 |
|---|---|
| 부모 | `POST /api/login {password}` → `token`. REST는 `Authorization: Bearer`, WS는 `/ws/parent?token=` |
| 에이전트 | `POST /api/agent/pair {code, platform, agentVersion, fcmToken?}` → `{deviceId, token, policy}`. 이후 `Authorization: Bearer <token>` |

페어링 코드는 6자리이고, 10분 동안 유효하며, 한 번만 쓸 수 있습니다. 부모앱에서 발급합니다.

## Policy

| 필드 | 타입 | 설명 |
|---|---|---|
| version | int | 변경 시마다 +1 |
| timezone | IANA string | 하루 경계(자정)·취침 시간 기준. 기본 `Asia/Seoul` |
| dailyLimitMinutes | int \| null | 하루 허용 시간. null이면 무제한 |
| bedtime | `{start:"HH:mm", end:"HH:mm"}` \| null | 자정을 넘기는 구간 허용 |
| blockedApps | string[] | Android: 패키지명 / Windows: `xxx.exe` (대소문자 무시) |
| alwaysAllowed | string[] | 잠금 중에도 허용하는 앱 (Android) |
| manualLock | bool | 부모 즉시 잠금 |
| extraTime | `{date, minutes}` \| null | `date`가 기기 기준 오늘일 때만 한도에 더함 |
| study | `{apps[], sites[], schedule[]}` | 학습 모드 설정. 아래 참고 |
| studySession | `{until(ISO8601), by:"parent"\|"child"}` \| null | 시간표와 별개로 진행 중인 학습 세션 |
| grants | `{date, apps[], sites[]}` \| null | 요청 승인으로 **오늘만** 허용된 앱·사이트 |

### 학습 모드 (study)

학습 모드에서는 **허용 목록에 있는 앱·사이트만** 쓸 수 있다 (차단 목록 방식이 아님).

| 필드 | 설명 |
|---|---|
| apps | Android: 패키지명 / Windows: `chrome.exe`(이름) 또는 전체 경로. Windows에서 이름만 쓴 항목은 사용자 쓰기 가능 폴더(`C:\Users`, `ProgramData`, Temp) 밖의 실행 파일에만 일치(이름 위장 방지) |
| sites | 도메인(하위 도메인 포함). Chrome/Edge URL 필터 형식. 서버가 `https://www.x.com/` → `x.com`으로 정규화하고, `*ebs*` 같은 중간 와일드카드는 거부 |
| schedule | `[{days:[1..7], start:"HH:mm", end:"HH:mm"}]`. ISO 요일(1=월). start < end (자정 넘김 없음) |

학습 모드 = `studySession.until > 지금` **또는** 지금이 schedule 칸 안.
허용 목록 = `study.*` + `grants.*`(grants.date가 오늘일 때).
학습 모드에서 쓴 시간은 `studyMinutes`로 따로 세고, **하루 한도는 자유 시간(전체 - 학습)에만 적용**한다.

### 판정 순서 (LimitEvaluator, 세 플랫폼 동일)

1. 변조 감지(tamper) → 잠금 `tamper`
2. `manualLock` → 잠금 `manual`
3. 취침 시간대 → 잠금 `bedtime`
4. 학습 모드 → **학습** (허용 목록만, 하루 한도로 잠그지 않음)
5. `자유 시간 >= dailyLimitMinutes + extraTime(오늘)` → 잠금 `daily_limit`
6. 위에 해당하지 않으면 자유

## 메시지

### 서버 → 에이전트
- `{type:"policy", policy}`: 접속 시(버전 불일치) 및 정책 변경 시 전송
- `{type:"request_result", requestId, kind, value, decision}`: 아이의 요청 처리 결과. decision = `today`|`always`|`deny`|`invalid`(주소 형식 오류, 즉시 응답)

### 에이전트 → 서버
| type | 필드 | 주기 |
|---|---|---|
| hello | agentVersion, policyVersion | 접속 직후 |
| usage | date, usedMinutes(전체), perApp, locked, lockReason, studyMinutes, mode(`free`\|`study`\|`locked`) | 1분 |
| apps | apps[{id, label}] — Android: 설치 앱 / Windows: 창을 띄운 적 있는 프로그램 | 접속 시·30분 (Windows는 새 프로그램 발견 시 1분 이내) |
| access_request | kind(`site`\|`app`), value, note | 아이가 요청할 때 |
| study_start | minutes(10~240) | 아이가 "공부 시작"할 때 (끝내기는 부모만) |
| processes | names[] (Windows) | 1분 |
| event | kind, detail, at(ISO8601) | 발생 시 |
| goodbye | reason | 정상 종료 직전 (오프라인 경고 억제) |

`event.kind` 종류: `process_killed`, `app_blocked`, `usage_access_revoked`, `device_owner_lost`, `tamper_suspected`, `session_disconnected`, `lock_started`, `lock_ended`, `study_started`, `study_ended`

부모에게 **즉시 푸시**하는 것은 보호 장치가 깨졌을 수 있는 `usage_access_revoked`, `device_owner_lost`, `tamper_suspected`와 아이의 요청뿐이다.
차단 시도(`process_killed` 등)는 시스템이 이미 막았으므로 실시간 알림 없이 **하루 요약**(기본 21:30, `SUMMARY_TIME`)에 횟수만 포함한다.

### 서버 → 부모
- `{type:"device_status", deviceId}`: 부모앱이 해당 기기를 REST로 다시 조회
- `{type:"device_event", deviceId, kind, detail, at}`
- `{type:"requests_changed"}`: 요청 목록을 다시 조회

## 부모 REST (학습 모드 관련)

| 메서드 | 경로 | 설명 |
|---|---|---|
| PUT | `/api/devices/:id/policy` | `{study:{apps, sites, schedule}}` 포함 부분 수정 |
| POST | `/api/devices/:id/study` | `{minutes}` 지금 학습 시작(1~480) / `0`이면 종료 |
| GET | `/api/devices/:id/apps` | 기기가 보고한 앱 목록 |
| GET | `/api/requests?status=pending\|all` | 아이의 요청 목록 |
| POST | `/api/requests/:rid` | `{decision:"today"\|"always"\|"deny"}`. 이미 처리된 요청은 409 |

### FCM (Android 깨우기)
- data 메시지 `{type:"sync"}`, priority high → 에이전트가 서비스를 깨워 WS에 재접속
